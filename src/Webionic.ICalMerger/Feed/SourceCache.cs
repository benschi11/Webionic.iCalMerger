using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Webionic.ICalMerger.Fetching;
using Webionic.ICalMerger.Merging;

namespace Webionic.ICalMerger.Feed;

/// <param name="Ics">Letzter bekannter gültiger Inhalt, sonst null.</param>
/// <param name="Error">Fehler des letzten Abrufs, sonst null.</param>
/// <param name="Stale">True, wenn Ics veraltet ist, weil der letzte Abruf fehlschlug.</param>
/// <param name="AttemptedAt">Zeitpunkt des letzten echten Abrufversuchs, sonst null.</param>
/// <param name="SucceededAt">Zeitpunkt des letzten erfolgreichen Abrufs, sonst null.</param>
public sealed record SourceResult(string? Ics, string? Error, bool Stale, DateTimeOffset? AttemptedAt = null, DateTimeOffset? SucceededAt = null);

public interface ISourceCache
{
    Task<SourceResult> GetAsync(string url, CancellationToken ct);
}

public sealed record SourceCacheOptions(TimeSpan Ttl, TimeSpan FailureBackoff)
{
    /// <summary>Einträge, auf die so lange niemand zugegriffen hat, werden verworfen.</summary>
    public TimeSpan IdleEviction { get; init; } = TimeSpan.FromDays(7);

    public static SourceCacheOptions Default { get; } = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
}

public sealed class SourceCache(ICalendarFetcher fetcher, TimeProvider time, SourceCacheOptions? options = null, ILogger<SourceCache>? logger = null) : ISourceCache
{
    private readonly SourceCacheOptions _options = options ?? SourceCacheOptions.Default;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private readonly object _sweepLock = new();
    private DateTimeOffset _lastSweep = time.GetUtcNow();

    internal int EntryCount => _entries.Count;

    public async Task<SourceResult> GetAsync(string url, CancellationToken ct)
    {
        SweepIdle();

        Entry entry;
        while (true)
        {
            entry = _entries.GetOrAdd(url, _ => new Entry());
            entry.LastAccessedAt = time.GetUtcNow();

            // Ein gleichzeitiger Abruf pro URL: Wartende sehen danach den frischen Eintrag.
            await entry.Gate.WaitAsync(ct);
            if (!entry.Evicted) break;

            // Der Eintrag wurde zwischen GetOrAdd und Gate entfernt: neuen holen.
            entry.Gate.Release();
        }

        try
        {
            var now = time.GetUtcNow();
            entry.LastAccessedAt = now;

            if (entry.LastError is null && entry.Ics is not null && now - entry.FetchedAt < _options.Ttl)
            {
                return new SourceResult(entry.Ics, null, false, entry.AttemptedAt, entry.FetchedAt);
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
                entry.AttemptedAt = now;
                entry.LastError = null;
                return new SourceResult(ics, null, false, now, now);
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
        entry.AttemptedAt = now;
        return Failed(entry);
    }

    private static SourceResult Failed(Entry entry) => new(
        entry.Ics, entry.LastError, entry.Ics is not null, entry.AttemptedAt, entry.Ics is not null ? entry.FetchedAt : null);

    /// <summary>Entfernt verwaiste Einträge, höchstens einmal pro Minute. Nur mit gehaltenem Gate, nie bei laufendem Abruf.</summary>
    private void SweepIdle()
    {
        var now = time.GetUtcNow();
        lock (_sweepLock)
        {
            if (now - _lastSweep < TimeSpan.FromMinutes(1)) return;
            _lastSweep = now;
        }

        foreach (var pair in _entries)
        {
            var entry = pair.Value;
            if (now - entry.LastAccessedAt < _options.IdleEviction || !entry.Gate.Wait(0)) continue;
            try
            {
                if (now - entry.LastAccessedAt >= _options.IdleEviction)
                {
                    entry.Evicted = true;
                    _entries.TryRemove(pair);
                }
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? Ics { get; set; }
        public DateTimeOffset FetchedAt { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset FailedAt { get; set; }
        public DateTimeOffset? AttemptedAt { get; set; }
        public DateTimeOffset LastAccessedAt { get; set; }
        public bool Evicted { get; set; }
    }
}
