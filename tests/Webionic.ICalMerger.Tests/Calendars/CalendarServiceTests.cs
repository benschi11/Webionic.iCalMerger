using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Webionic.ICalMerger.Calendars;
using Webionic.ICalMerger.Tests.Support;
using static Webionic.ICalMerger.Tests.Support.IcsFixtures;

namespace Webionic.ICalMerger.Tests.Calendars;

public sealed class CalendarServiceTests : IDisposable
{
    private const string UrlA = "https://example.com/a.ics";
    private const string UrlB = "https://example.com/b.ics";

    private readonly TestDb _db = new();
    private readonly FakeFetcher _fetcher = new();
    private readonly CalendarService _service;
    private readonly string _alice;
    private readonly string _bob;

    public CalendarServiceTests()
    {
        _alice = _db.AddUserAsync("alice@example.com").GetAwaiter().GetResult();
        _bob = _db.AddUserAsync("bob@example.com").GetAwaiter().GetResult();
        _fetcher.Set(UrlA, Calendar(Event("a@x")));
        _fetcher.Set(UrlB, Calendar(Event("b@x")));
        _service = new CalendarService(_db, _fetcher,
            Options.Create(new AppLimits { MaxCalendarsPerUser = 2, MaxSourcesPerCalendar = 2 }));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_StoresCalendarWithRandomToken()
    {
        var first = await _service.CreateAsync(_alice, "  Familie  ");
        var second = await _service.CreateAsync(_alice, "Arbeit");

        Assert.Equal("Familie", first.Name);
        Assert.Equal(43, first.Token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", first.Token);
        Assert.NotEqual(first.Token, second.Token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_RejectsEmptyName(string name)
    {
        await Assert.ThrowsAsync<DomainException>(() => _service.CreateAsync(_alice, name));
    }

    [Fact]
    public async Task Create_RejectsTooLongName()
    {
        await Assert.ThrowsAsync<DomainException>(() => _service.CreateAsync(_alice, new string('x', 101)));
    }

    [Fact]
    public async Task Create_EnforcesCalendarLimitPerUser()
    {
        await _service.CreateAsync(_alice, "1");
        await _service.CreateAsync(_alice, "2");

        var ex = await Assert.ThrowsAsync<DomainException>(() => _service.CreateAsync(_alice, "3"));
        Assert.Contains("2", ex.Message);

        await _service.CreateAsync(_bob, "Bobs erster");
    }

    [Fact]
    public async Task List_ReturnsOnlyOwnCalendarsWithSources()
    {
        var mine = await _service.CreateAsync(_alice, "Meiner");
        await _service.CreateAsync(_bob, "Fremder");
        await _service.AddSourceAsync(_alice, mine.Id, "A", UrlA);

        var list = await _service.ListAsync(_alice);

        var only = Assert.Single(list);
        Assert.Equal("Meiner", only.Name);
        Assert.Single(only.Sources);
    }

    [Fact]
    public async Task Get_OfForeignCalendar_ReturnsNull()
    {
        var bobs = await _service.CreateAsync(_bob, "Bobs");

        Assert.Null(await _service.GetAsync(_alice, bobs.Id));
        Assert.NotNull(await _service.GetAsync(_bob, bobs.Id));
    }

    [Fact]
    public async Task MutationsOnForeignCalendar_AreRejected()
    {
        var bobs = await _service.CreateAsync(_bob, "Bobs");

        await Assert.ThrowsAsync<DomainException>(() => _service.RenameAsync(_alice, bobs.Id, "Meins"));
        await Assert.ThrowsAsync<DomainException>(() => _service.DeleteAsync(_alice, bobs.Id));
        await Assert.ThrowsAsync<DomainException>(() => _service.RegenerateTokenAsync(_alice, bobs.Id));
        await Assert.ThrowsAsync<DomainException>(() => _service.AddSourceAsync(_alice, bobs.Id, "A", UrlA));
        Assert.Equal("Bobs", (await _service.GetAsync(_bob, bobs.Id))!.Name);
    }

    [Fact]
    public async Task Rename_ChangesName()
    {
        var calendar = await _service.CreateAsync(_alice, "Alt");

        await _service.RenameAsync(_alice, calendar.Id, "Neu");

        Assert.Equal("Neu", (await _service.GetAsync(_alice, calendar.Id))!.Name);
    }

    [Fact]
    public async Task RegenerateToken_ReplacesToken()
    {
        var calendar = await _service.CreateAsync(_alice, "K");

        var newToken = await _service.RegenerateTokenAsync(_alice, calendar.Id);

        Assert.NotEqual(calendar.Token, newToken);
        Assert.Equal(newToken, (await _service.GetAsync(_alice, calendar.Id))!.Token);
    }

    [Fact]
    public async Task AddSource_NormalizesUrlAndAppendsSortOrder()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        _fetcher.Set("https://example.com/w.ics", Calendar(Event("w@x")));

        var first = await _service.AddSourceAsync(_alice, calendar.Id, "Webcal", "webcal://example.com/w.ics");
        var second = await _service.AddSourceAsync(_alice, calendar.Id, "B", UrlB);

        Assert.Equal("https://example.com/w.ics", first.Url);
        Assert.True(second.SortOrder > first.SortOrder);
    }

    [Fact]
    public async Task AddSource_WithoutName_UsesHost()
    {
        var calendar = await _service.CreateAsync(_alice, "K");

        var source = await _service.AddSourceAsync(_alice, calendar.Id, "  ", UrlA);

        Assert.Equal("example.com", source.Name);
    }

    [Fact]
    public async Task AddSource_EnforcesSourceLimit()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        await _service.AddSourceAsync(_alice, calendar.Id, "A", UrlA);
        await _service.AddSourceAsync(_alice, calendar.Id, "B", UrlB);

        await Assert.ThrowsAsync<DomainException>(() => _service.AddSourceAsync(_alice, calendar.Id, "C", UrlA));
    }

    [Fact]
    public async Task AddSource_RejectsInvalidUrl()
    {
        var calendar = await _service.CreateAsync(_alice, "K");

        var ex = await Assert.ThrowsAsync<DomainException>(() => _service.AddSourceAsync(_alice, calendar.Id, "X", "ftp://x"));

        Assert.Equal("Ungültige URL", ex.Message);
    }

    [Fact]
    public async Task AddSource_RejectsUnreachableUrl()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        _fetcher.Fail("https://example.com/down.ics", "HTTP 500");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.AddSourceAsync(_alice, calendar.Id, "X", "https://example.com/down.ics"));

        Assert.Contains("HTTP 500", ex.Message);
    }

    [Fact]
    public async Task AddSource_RejectsHtmlResponse()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        _fetcher.Set("https://example.com/login", "<html><body>Bitte anmelden</body></html>");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.AddSourceAsync(_alice, calendar.Id, "X", "https://example.com/login"));

        Assert.Contains("keinen iCal-Kalender", ex.Message);
        Assert.Empty((await _service.GetAsync(_alice, calendar.Id))!.Sources);
    }

    [Theory]
    [InlineData("﻿BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n")]
    [InlineData("﻿\r\n  \r\nBEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n")]
    [InlineData("  \n  BEGIN:VCALENDAR\nEND:VCALENDAR\n")]
    public async Task AddSource_AcceptsCalendarWithLeadingBomOrWhitespace(string ics)
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        _fetcher.Set("https://example.com/bom.ics", ics);

        var source = await _service.AddSourceAsync(_alice, calendar.Id, "Bom", "https://example.com/bom.ics");

        Assert.Equal("Bom", source.Name);
    }

    [Theory]
    [InlineData("﻿<!DOCTYPE html><html><body>Login</body></html>")]
    [InlineData("﻿\r\n  <html><body>Login</body></html>")]
    public async Task AddSource_RejectsHtmlWithLeadingBom(string html)
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        _fetcher.Set("https://example.com/login", html);

        await Assert.ThrowsAsync<DomainException>(() =>
            _service.AddSourceAsync(_alice, calendar.Id, "X", "https://example.com/login"));
    }

    [Fact]
    public async Task UpdateSource_ChangesNameAndUrl_AndRevalidates()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        var source = await _service.AddSourceAsync(_alice, calendar.Id, "A", UrlA);

        await _service.UpdateSourceAsync(_alice, source.Id, "Neu", UrlB);

        var updated = Assert.Single((await _service.GetAsync(_alice, calendar.Id))!.Sources);
        Assert.Equal("Neu", updated.Name);
        Assert.Equal(UrlB, updated.Url);

        await Assert.ThrowsAsync<DomainException>(() => _service.UpdateSourceAsync(_alice, source.Id, "X", "ftp://x"));
    }

    [Fact]
    public async Task SourceMutationsOnForeignSource_AreRejected()
    {
        var bobs = await _service.CreateAsync(_bob, "Bobs");
        var source = await _service.AddSourceAsync(_bob, bobs.Id, "A", UrlA);

        await Assert.ThrowsAsync<DomainException>(() => _service.UpdateSourceAsync(_alice, source.Id, "X", UrlB));
        await Assert.ThrowsAsync<DomainException>(() => _service.DeleteSourceAsync(_alice, source.Id));
        await Assert.ThrowsAsync<DomainException>(() => _service.MoveSourceAsync(_alice, source.Id, 1));
        Assert.Single((await _service.GetAsync(_bob, bobs.Id))!.Sources);
    }

    [Fact]
    public async Task DeleteSource_RemovesIt()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        var source = await _service.AddSourceAsync(_alice, calendar.Id, "A", UrlA);

        await _service.DeleteSourceAsync(_alice, source.Id);

        Assert.Empty((await _service.GetAsync(_alice, calendar.Id))!.Sources);
    }

    [Fact]
    public async Task MoveSource_SwapsWithNeighbourAndIgnoresBounds()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        var a = await _service.AddSourceAsync(_alice, calendar.Id, "A", UrlA);
        var b = await _service.AddSourceAsync(_alice, calendar.Id, "B", UrlB);

        await _service.MoveSourceAsync(_alice, b.Id, -1);
        Assert.Equal(new[] { "B", "A" }, (await _service.GetAsync(_alice, calendar.Id))!.Sources.Select(s => s.Name));

        await _service.MoveSourceAsync(_alice, b.Id, -1); // schon ganz oben: keine Änderung
        Assert.Equal(new[] { "B", "A" }, (await _service.GetAsync(_alice, calendar.Id))!.Sources.Select(s => s.Name));

        await _service.MoveSourceAsync(_alice, a.Id, 1); // schon ganz unten: keine Änderung
        Assert.Equal(new[] { "B", "A" }, (await _service.GetAsync(_alice, calendar.Id))!.Sources.Select(s => s.Name));
    }

    [Fact]
    public async Task DeleteCalendar_CascadesToSources()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        await _service.AddSourceAsync(_alice, calendar.Id, "A", UrlA);

        await _service.DeleteAsync(_alice, calendar.Id);

        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.Calendars.CountAsync());
        Assert.Equal(0, await db.Sources.CountAsync());
    }
}
