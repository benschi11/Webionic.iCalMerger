using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Webionic.ICalMerger.Calendars;
using Webionic.ICalMerger.Data;
using Webionic.ICalMerger.Feed;
using Webionic.ICalMerger.Tests.Support;
using static Webionic.ICalMerger.Tests.Support.IcsFixtures;

namespace Webionic.ICalMerger.Tests.Feed;

public sealed class FeedServiceTests : IDisposable
{
    private const string UrlA = "https://example.com/a.ics";
    private const string UrlB = "https://example.com/b.ics";

    private readonly TestDb _db = new();
    private readonly FakeFetcher _fetcher = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly CalendarService _calendars;
    private readonly FeedService _feed;
    private string _owner = "";

    public FeedServiceTests()
    {
        _fetcher.Set(UrlA, Calendar(Event("a@x", "Von A")));
        _fetcher.Set(UrlB, Calendar(Event("b@x", "Von B")));
        _calendars = new CalendarService(_db, _fetcher, Options.Create(new AppLimits()));
        _feed = new FeedService(_db, new SourceCache(_fetcher, _time), _time);
    }

    public void Dispose() => _db.Dispose();

    private async Task<MergedCalendar> CalendarWithSourcesAsync(params string[] urls)
    {
        _owner = await _db.AddUserAsync("owner@example.com");
        var calendar = await _calendars.CreateAsync(_owner, "Familie");
        foreach (var url in urls)
        {
            await _calendars.AddSourceAsync(_owner, calendar.Id, url, url);
        }
        return calendar;
    }

    [Fact]
    public async Task Build_UnknownToken_ReturnsNull()
    {
        Assert.Null(await _feed.BuildAsync("gibt-es-nicht", default));
    }

    [Fact]
    public async Task Build_MergesAllSources()
    {
        var calendar = await CalendarWithSourcesAsync(UrlA, UrlB);

        var result = await _feed.BuildAsync(calendar.Token, default);

        Assert.NotNull(result?.Ics);
        Assert.Contains("UID:a@x", result.Ics);
        Assert.Contains("UID:b@x", result.Ics);
        Assert.Contains("X-WR-CALNAME:Familie", result.Ics);
        Assert.Equal(0, result.FailedSources);
    }

    [Fact]
    public async Task Build_UsesSortOrderAsPriorityForDuplicates()
    {
        _fetcher.Set(UrlA, Calendar(Event("dup@x", "Aus A")));
        _fetcher.Set(UrlB, Calendar(Event("dup@x", "Aus B")));
        var calendar = await CalendarWithSourcesAsync(UrlA, UrlB);
        var sourceB = (await _calendars.GetAsync(_owner, calendar.Id))!.Sources[1];
        await _calendars.MoveSourceAsync(_owner, sourceB.Id, -1);

        var result = await _feed.BuildAsync(calendar.Token, default);

        Assert.Contains("SUMMARY:Aus B", result!.Ics);
        Assert.DoesNotContain("SUMMARY:Aus A", result.Ics);
    }

    [Fact]
    public async Task Build_PartialFailure_ReturnsRestAndStoresStatus()
    {
        var calendar = await CalendarWithSourcesAsync(UrlA, UrlB);
        _fetcher.Fail(UrlB, "HTTP 500");

        var result = await _feed.BuildAsync(calendar.Token, default);

        Assert.Contains("UID:a@x", result!.Ics);
        Assert.DoesNotContain("UID:b@x", result.Ics);
        Assert.Equal(1, result.FailedSources);

        var sources = (await _calendars.GetAsync(_owner, calendar.Id))!.Sources;
        Assert.Null(sources[0].LastError);
        Assert.NotNull(sources[0].LastSuccessAt);
        Assert.Equal("HTTP 500", sources[1].LastError);
        Assert.Null(sources[1].LastSuccessAt);
        Assert.NotNull(sources[1].LastAttemptAt);
    }

    [Fact]
    public async Task Build_AllSourcesFail_ReturnsNullIcs()
    {
        var calendar = await CalendarWithSourcesAsync(UrlA, UrlB);
        _fetcher.Fail(UrlA);
        _fetcher.Fail(UrlB);

        var result = await _feed.BuildAsync(calendar.Token, default);

        Assert.NotNull(result);
        Assert.Null(result.Ics);
        Assert.Equal(2, result.FailedSources);
    }

    [Fact]
    public async Task Build_NoSources_ReturnsEmptyValidCalendar()
    {
        var calendar = await CalendarWithSourcesAsync();

        var result = await _feed.BuildAsync(calendar.Token, default);

        Assert.StartsWith("BEGIN:VCALENDAR", result!.Ics);
        Assert.EndsWith("END:VCALENDAR\r\n", result.Ics);
        Assert.DoesNotContain("BEGIN:VEVENT", result.Ics);
    }

    [Fact]
    public async Task Build_AfterSourceGoesDown_StillServesLastKnownData()
    {
        var calendar = await CalendarWithSourcesAsync(UrlA);
        await _feed.BuildAsync(calendar.Token, default);

        _time.Advance(TimeSpan.FromMinutes(6));
        _fetcher.Fail(UrlA, "HTTP 503");
        var result = await _feed.BuildAsync(calendar.Token, default);

        Assert.Contains("UID:a@x", result!.Ics);
        var source = Assert.Single((await _calendars.GetAsync(_owner, calendar.Id))!.Sources);
        Assert.Equal("HTTP 503", source.LastError);
    }

    [Fact]
    public async Task Build_BrokenSource_DoesNotBreakFeed()
    {
        var calendar = await CalendarWithSourcesAsync(UrlA, UrlB);
        _fetcher.SetAsync(UrlB, _ => throw new InvalidOperationException("boom"));

        var result = await _feed.BuildAsync(calendar.Token, default);

        Assert.Contains("UID:a@x", result!.Ics);
        Assert.Equal(1, result.FailedSources);
    }

    [Fact]
    public async Task Build_HtmlSource_IsErrorAndNotMerged()
    {
        var calendar = await CalendarWithSourcesAsync(UrlA, UrlB);
        _fetcher.Set(UrlB, "<html>Login</html>");

        var result = await _feed.BuildAsync(calendar.Token, default);

        Assert.Equal(1, result!.FailedSources);
        Assert.DoesNotContain("html", result.Ics, StringComparison.OrdinalIgnoreCase);
        var sources = (await _calendars.GetAsync(_owner, calendar.Id))!.Sources;
        Assert.Equal("Keine gültige iCal-Datei", sources[1].LastError);
    }

    [Fact]
    public async Task Build_StatusPersistenceFailure_StillReturnsFeed()
    {
        var calendar = await CalendarWithSourcesAsync(UrlA);
        var feed = new FeedService(new FailingOnSaveFactory(_db), new SourceCache(_fetcher, _time), _time);

        var result = await feed.BuildAsync(calendar.Token, default);

        Assert.Contains("UID:a@x", result!.Ics);
    }

    private sealed class FailingOnSaveFactory(TestDb inner) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext()
        {
            using var probe = inner.CreateDbContext();
            return new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(probe.Database.GetDbConnection())
                .UseApplicationServiceProvider(DesignTimeDbContextFactory.IdentitySchemaServices())
                .AddInterceptors(new ThrowOnSave())
                .Options);
        }
    }

    private sealed class ThrowOnSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default) =>
            throw new DbUpdateException("kaputt");
    }
}
