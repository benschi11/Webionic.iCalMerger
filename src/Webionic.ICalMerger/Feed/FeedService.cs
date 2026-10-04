using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Webionic.ICalMerger.Data;
using Webionic.ICalMerger.Merging;

namespace Webionic.ICalMerger.Feed;

/// <param name="Ics">Fertiger Kalender. Null, wenn alle Quellen ausgefallen sind und kein Cache existiert.</param>
public sealed record FeedResult(string? Ics, int FailedSources);

public sealed class FeedService(IDbContextFactory<ApplicationDbContext> dbFactory, ISourceCache cache, TimeProvider time, ILogger<FeedService>? logger = null)
{
    /// <returns>Null, wenn das Token unbekannt ist.</returns>
    public async Task<FeedResult?> BuildAsync(string token, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var calendar = await db.Calendars.Include(c => c.Sources).FirstOrDefaultAsync(c => c.Token == token, ct);
        if (calendar is null) return null;

        var sources = calendar.Sources.OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToList();
        var results = await Task.WhenAll(sources.Select(s => cache.GetAsync(s.Url, ct)));

        var now = time.GetUtcNow().UtcDateTime;
        for (var i = 0; i < sources.Count; i++)
        {
            sources[i].LastAttemptAt = now;
            if (results[i].Error is null)
            {
                sources[i].LastSuccessAt = now;
                sources[i].LastError = null;
            }
            else
            {
                var error = results[i].Error!;
                sources[i].LastError = error.Length > 500 ? error[..500] : error;
            }
        }
        try
        {
            await db.SaveChangesAsync(ct);
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
