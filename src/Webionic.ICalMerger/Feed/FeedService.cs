using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Webionic.ICalMerger.Data;
using Webionic.ICalMerger.Merging;

namespace Webionic.ICalMerger.Feed;

/// <param name="Ics">Fertiger Kalender. Null, wenn alle Quellen ausgefallen sind und kein Cache existiert.</param>
public sealed record FeedResult(string? Ics, int FailedSources);

public sealed class FeedService(IDbContextFactory<ApplicationDbContext> dbFactory, ISourceCache cache, ILogger<FeedService>? logger = null)
{
    /// <returns>Null, wenn das Token unbekannt ist.</returns>
    public async Task<FeedResult?> BuildAsync(string token, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var calendar = await db.Calendars.Include(c => c.Sources).FirstOrDefaultAsync(c => c.Token == token, ct);
        if (calendar is null) return null;

        var sources = calendar.Sources.OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToList();
        var results = await Task.WhenAll(sources.Select(s => cache.GetAsync(s.Url, ct)));

        // Nur schreiben, was sich gegenüber dem gespeicherten Stand wirklich ändert (Cache-Treffer: nichts).
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var result = results[i];
            var attempted = result.AttemptedAt?.UtcDateTime;
            var succeeded = result.SucceededAt?.UtcDateTime;
            var error = result.Error is { Length: > 500 } e ? e[..500] : result.Error;

            if (attempted is not null && source.LastAttemptAt != attempted) source.LastAttemptAt = attempted;
            if (succeeded is not null && source.LastSuccessAt != succeeded) source.LastSuccessAt = succeeded;
            if (source.LastError != error) source.LastError = error;
        }
        try
        {
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Der Status ist nur Anzeige. Der Feed darf daran nicht scheitern.
            logger?.LogWarning("Quellstatus für Kalender {CalendarId} nicht gespeichert: {Message}", calendar.Id, ex.Message);
        }

        var available = results.Where(r => r.Ics is not null).Select(r => r.Ics!).ToList();
        var failed = results.Count(r => r.Error is not null);

        if (sources.Count > 0 && available.Count == 0)
        {
            return new FeedResult(null, failed);
        }

        return new FeedResult(IcsMerger.Merge(calendar.Name, available), failed);
    }
}
