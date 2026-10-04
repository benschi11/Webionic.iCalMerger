using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Webionic.ICalMerger.Data;
using Webionic.ICalMerger.Fetching;
using Webionic.ICalMerger.Merging;

namespace Webionic.ICalMerger.Calendars;

public sealed class CalendarService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICalendarFetcher fetcher,
    IOptions<AppLimits> limits)
{
    private const string CalendarNotFound = "Kalender nicht gefunden.";
    private const string SourceNotFound = "Quelle nicht gefunden.";

    public async Task<List<MergedCalendar>> ListAsync(string ownerId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var calendars = await db.Calendars.AsNoTracking().Include(c => c.Sources)
            .Where(c => c.OwnerId == ownerId).OrderBy(c => c.Name).ThenBy(c => c.Id).ToListAsync();
        calendars.ForEach(SortSources);
        return calendars;
    }

    public async Task<MergedCalendar?> GetAsync(string ownerId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var calendar = await db.Calendars.AsNoTracking().Include(c => c.Sources)
            .FirstOrDefaultAsync(c => c.Id == id && c.OwnerId == ownerId);
        if (calendar is not null) SortSources(calendar);
        return calendar;
    }

    public async Task<MergedCalendar> CreateAsync(string ownerId, string name)
    {
        name = ValidateName(name);
        await using var db = await dbFactory.CreateDbContextAsync();

        var count = await db.Calendars.CountAsync(c => c.OwnerId == ownerId);
        if (count >= limits.Value.MaxCalendarsPerUser)
        {
            throw new DomainException($"Du kannst höchstens {limits.Value.MaxCalendarsPerUser} Kalender anlegen.");
        }

        var calendar = new MergedCalendar
        {
            OwnerId = ownerId,
            Name = name,
            Token = TokenGenerator.NewToken(),
            CreatedAt = DateTime.UtcNow,
        };
        db.Calendars.Add(calendar);
        await db.SaveChangesAsync();
        return calendar;
    }

    public async Task RenameAsync(string ownerId, int id, string name)
    {
        name = ValidateName(name);
        await using var db = await dbFactory.CreateDbContextAsync();
        var calendar = await FindOwnedCalendarAsync(db, ownerId, id);
        calendar.Name = name;
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string ownerId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var calendar = await FindOwnedCalendarAsync(db, ownerId, id);
        db.Calendars.Remove(calendar);
        await db.SaveChangesAsync();
    }

    public async Task<string> RegenerateTokenAsync(string ownerId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var calendar = await FindOwnedCalendarAsync(db, ownerId, id);
        calendar.Token = TokenGenerator.NewToken();
        await db.SaveChangesAsync();
        return calendar.Token;
    }

    public async Task<CalendarSource> AddSourceAsync(string ownerId, int calendarId, string name, string url, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var calendar = await FindOwnedCalendarAsync(db, ownerId, calendarId, ct);

        var existing = await db.Sources.Where(s => s.MergedCalendarId == calendarId).Select(s => s.SortOrder).ToListAsync(ct);
        if (existing.Count >= limits.Value.MaxSourcesPerCalendar)
        {
            throw new DomainException($"Ein Kalender kann höchstens {limits.Value.MaxSourcesPerCalendar} Quellen haben.");
        }

        var uri = await ValidateSourceAsync(url, ct);
        var source = new CalendarSource
        {
            MergedCalendarId = calendar.Id,
            Name = ResolveSourceName(name, uri),
            Url = uri.AbsoluteUri,
            SortOrder = existing.Count == 0 ? 0 : existing.Max() + 1,
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync(ct);
        return source;
    }

    public async Task UpdateSourceAsync(string ownerId, int sourceId, string name, string url, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await FindOwnedSourceAsync(db, ownerId, sourceId, ct);

        var uri = await ValidateSourceAsync(url, ct);
        source.Name = ResolveSourceName(name, uri);
        source.Url = uri.AbsoluteUri;
        source.LastAttemptAt = null;
        source.LastSuccessAt = null;
        source.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteSourceAsync(string ownerId, int sourceId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var source = await FindOwnedSourceAsync(db, ownerId, sourceId);
        db.Sources.Remove(source);
        await db.SaveChangesAsync();
    }

    /// <param name="direction">-1 nach oben (höhere Priorität), +1 nach unten.</param>
    public async Task MoveSourceAsync(string ownerId, int sourceId, int direction)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var source = await FindOwnedSourceAsync(db, ownerId, sourceId);

        var siblings = await db.Sources.Where(s => s.MergedCalendarId == source.MergedCalendarId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync();
        var index = siblings.FindIndex(s => s.Id == source.Id);
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= siblings.Count) return;

        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        for (var i = 0; i < siblings.Count; i++) siblings[i].SortOrder = i;
        await db.SaveChangesAsync();
    }

    private async Task<Uri> ValidateSourceAsync(string url, CancellationToken ct)
    {
        if ((url ?? "").Length > 2000) throw new DomainException("Die URL ist zu lang.");

        Uri uri;
        try
        {
            uri = SafeHttpFetcher.NormalizeUrl(url!);
        }
        catch (FetchException ex)
        {
            throw new DomainException(ex.Message);
        }

        string ics;
        try
        {
            ics = await fetcher.FetchAsync(uri.AbsoluteUri, ct);
        }
        catch (FetchException ex)
        {
            throw new DomainException($"Die URL konnte nicht abgerufen werden: {ex.Message}");
        }

        if (!IcsMerger.LooksLikeCalendar(ics))
        {
            throw new DomainException("Die URL liefert keinen iCal-Kalender.");
        }

        return uri;
    }

    private static string ResolveSourceName(string? name, Uri uri)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) trimmed = uri.Host;
        if (trimmed.Length > 100) throw new DomainException("Der Name ist zu lang (höchstens 100 Zeichen).");
        return trimmed;
    }

    private static string ValidateName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) throw new DomainException("Bitte einen Namen eingeben.");
        if (trimmed.Length > 100) throw new DomainException("Der Name ist zu lang (höchstens 100 Zeichen).");
        return trimmed;
    }

    private static void SortSources(MergedCalendar calendar) =>
        calendar.Sources = calendar.Sources.OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToList();

    private static async Task<MergedCalendar> FindOwnedCalendarAsync(ApplicationDbContext db, string ownerId, int id, CancellationToken ct = default) =>
        await db.Calendars.FirstOrDefaultAsync(c => c.Id == id && c.OwnerId == ownerId, ct)
        ?? throw new DomainException(CalendarNotFound);

    private static async Task<CalendarSource> FindOwnedSourceAsync(ApplicationDbContext db, string ownerId, int id, CancellationToken ct = default) =>
        await db.Sources.FirstOrDefaultAsync(s => s.Id == id && s.MergedCalendar!.OwnerId == ownerId, ct)
        ?? throw new DomainException(SourceNotFound);
}
