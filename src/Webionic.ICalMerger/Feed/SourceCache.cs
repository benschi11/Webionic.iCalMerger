using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Webionic.ICalMerger.Fetching;
using Webionic.ICalMerger.Merging;

namespace Webionic.ICalMerger.Feed;

/// <param name="Ics">Letzter bekannter gültiger Inhalt, sonst null.</param>
/// <param name="Error">Fehler des letzten Abrufs, sonst null.</param>
/// <param name="Stale">True, wenn Ics veraltet ist, weil der letzte Abruf fehlschlug.</param>
public sealed record SourceResult(string? Ics, string? Error, bool Stale);

public interface ISourceCache
{
    Task<SourceResult> GetAsync(string url, CancellationToken ct);
}

public sealed record SourceCacheOptions(TimeSpan Ttl, TimeSpan FailureBackoff)
{
    public static SourceCacheOptions Default { get; } = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
}

public sealed class SourceCache(ICalendarFetcher fetcher, TimeProvider time, SourceCacheOptions? options = null, ILogger<SourceCache>? logger = null) : ISourceCache
{
    private readonly SourceCacheOptions _options = options ?? SourceCacheOptions.Default;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<SourceResult> GetAsync(string url, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(url, _ => new Entry());

        // Ein gleichzeitiger Abruf pro URL: Wartende sehen danach den frischen Eintrag.
        await entry.Gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();

            if (entry.LastError is null && entry.Ics is not null && now - entry.FetchedAt < _options.Ttl)
            {
                return new SourceResult(entry.Ics, null, false);
            }

            if (entry.LastError is not null && now - entry.FailedAt < _options.FailureBackoff)
            {
                return Failed(entry);
            }

            try
            {
                var ics = await fetcher.FetchAsync(url, ct);
                if (!IcsMerger.LooksLikeCalendar(ics))
                {
                    throw new FetchException("Keine gültige iCal-Datei");
                }

                entry.Ics = ics;
                entry.FetchedAt = now;
                entry.LastError = null;
                return new SourceResult(ics, null, false);
            }
            catch (FetchException ex)
            {
                return Fail(entry, ex.Message, now);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Nur der Typ wird geloggt: Meldungen fremder Exceptions können die URL enthalten.
                logger?.LogWarning("Unerwarteter Fehler beim Abruf einer Quelle: {ExceptionType}", ex.GetType().Name);
                return Fail(entry, "Abruf fehlgeschlagen", now);
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static SourceResult Fail(Entry entry, string error, DateTimeOffset now)
    {
        entry.LastError = error;
        entry.FailedAt = now;
        return Failed(entry);
    }

    private static SourceResult Failed(Entry entry) => new(entry.Ics, entry.LastError, entry.Ics is not null);

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? Ics { get; set; }
        public DateTimeOffset FetchedAt { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset FailedAt { get; set; }
    }
}
