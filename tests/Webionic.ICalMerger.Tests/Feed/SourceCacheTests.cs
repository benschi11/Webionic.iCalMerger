using Microsoft.Extensions.Time.Testing;
using Webionic.ICalMerger.Feed;
using Webionic.ICalMerger.Tests.Support;
using static Webionic.ICalMerger.Tests.Support.IcsFixtures;

namespace Webionic.ICalMerger.Tests.Feed;

public class SourceCacheTests
{
    private const string Url = "https://example.com/a.ics";

    private readonly FakeFetcher _fetcher = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly SourceCache _cache;

    public SourceCacheTests()
    {
        _cache = new SourceCache(_fetcher, _time);
    }

    [Fact]
    public async Task Get_FetchesOnceWithinTtl()
    {
        _fetcher.Set(Url, Calendar(Event("a@x")));

        var first = await _cache.GetAsync(Url, default);
        _time.Advance(TimeSpan.FromMinutes(4));
        var second = await _cache.GetAsync(Url, default);

        Assert.Equal(1, _fetcher.CallCount(Url));
        Assert.Equal(first.Ics, second.Ics);
        Assert.Null(second.Error);
        Assert.False(second.Stale);
    }

    [Fact]
    public async Task Get_RefetchesAfterTtl()
    {
        _fetcher.Set(Url, Calendar(Event("a@x")));
        await _cache.GetAsync(Url, default);

        _time.Advance(TimeSpan.FromMinutes(6));
        _fetcher.Set(Url, Calendar(Event("neu@x")));
        var result = await _cache.GetAsync(Url, default);

        Assert.Equal(2, _fetcher.CallCount(Url));
        Assert.Contains("neu@x", result.Ics);
    }

    [Fact]
    public async Task Get_FailureWithoutCache_ReturnsErrorOnly()
    {
        _fetcher.Fail(Url, "HTTP 500");

        var result = await _cache.GetAsync(Url, default);

        Assert.Null(result.Ics);
        Assert.Equal("HTTP 500", result.Error);
        Assert.False(result.Stale);
    }

    [Fact]
    public async Task Get_FailureAfterSuccess_ServesStaleData()
    {
        _fetcher.Set(Url, Calendar(Event("alt@x")));
        await _cache.GetAsync(Url, default);

        _time.Advance(TimeSpan.FromMinutes(6));
        _fetcher.Fail(Url, "HTTP 503");
        var result = await _cache.GetAsync(Url, default);

        Assert.Contains("alt@x", result.Ics);
        Assert.Equal("HTTP 503", result.Error);
        Assert.True(result.Stale);
    }

    [Fact]
    public async Task Get_DoesNotHammerFailingSource()
    {
        _fetcher.Fail(Url);

        await _cache.GetAsync(Url, default);
        _time.Advance(TimeSpan.FromSeconds(30));
        await _cache.GetAsync(Url, default);
        Assert.Equal(1, _fetcher.CallCount(Url));

        _time.Advance(TimeSpan.FromSeconds(31));
        await _cache.GetAsync(Url, default);
        Assert.Equal(2, _fetcher.CallCount(Url));
    }

    [Fact]
    public async Task Get_RecoversAfterFailure()
    {
        _fetcher.Fail(Url);
        await _cache.GetAsync(Url, default);

        _time.Advance(TimeSpan.FromMinutes(2));
        _fetcher.Set(Url, Calendar(Event("ok@x")));
        var result = await _cache.GetAsync(Url, default);

        Assert.Contains("ok@x", result.Ics);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Get_TreatsHtmlResponseAsFailureAndDoesNotCacheIt()
    {
        _fetcher.Set(Url, "<html><body>Bitte anmelden</body></html>");

        var result = await _cache.GetAsync(Url, default);

        Assert.Null(result.Ics);
        Assert.Equal("Keine gültige iCal-Datei", result.Error);
    }

    [Fact]
    public async Task Get_HtmlAfterGoodData_KeepsServingGoodData()
    {
        _fetcher.Set(Url, Calendar(Event("gut@x")));
        await _cache.GetAsync(Url, default);

        _time.Advance(TimeSpan.FromMinutes(6));
        _fetcher.Set(Url, "<html>Captive Portal</html>");
        var result = await _cache.GetAsync(Url, default);

        Assert.Contains("gut@x", result.Ics);
        Assert.True(result.Stale);
    }

    [Fact]
    public async Task Get_CoalescesConcurrentRequestsForSameUrl()
    {
        var release = new TaskCompletionSource();
        _fetcher.SetAsync(Url, async _ =>
        {
            await release.Task;
            return Calendar(Event("a@x"));
        });

        var tasks = Enumerable.Range(0, 5).Select(_ => _cache.GetAsync(Url, default)).ToArray();
        await Task.Delay(100);
        release.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, _fetcher.CallCount(Url));
        Assert.All(results, r => Assert.Contains("a@x", r.Ics));
    }

    [Fact]
    public async Task Get_KeepsUrlsIndependent()
    {
        const string other = "https://example.com/b.ics";
        _fetcher.Set(Url, Calendar(Event("a@x")));
        _fetcher.Fail(other);

        var good = await _cache.GetAsync(Url, default);
        var bad = await _cache.GetAsync(other, default);

        Assert.NotNull(good.Ics);
        Assert.Null(bad.Ics);
    }

    [Fact]
    public async Task Get_AcceptsBodyWithBomAndLeadingBlankLines()
    {
        _fetcher.Set(Url, "\uFEFF\r\n" + Calendar(Event("bom@x")));

        var result = await _cache.GetAsync(Url, default);

        Assert.Null(result.Error);
        Assert.Contains("bom@x", result.Ics);
    }

    [Fact]
    public async Task Get_UnexpectedFetcherException_BecomesGenericError()
    {
        _fetcher.SetAsync(Url, _ => throw new InvalidOperationException("https://geheim.example/token"));

        var result = await _cache.GetAsync(Url, default);

        Assert.Null(result.Ics);
        Assert.Equal("Abruf fehlgeschlagen", result.Error);
    }

    [Fact]
    public async Task Get_UnexpectedFetcherException_AfterSuccess_ServesStale()
    {
        _fetcher.Set(Url, Calendar(Event("alt@x")));
        await _cache.GetAsync(Url, default);

        _time.Advance(TimeSpan.FromMinutes(6));
        _fetcher.SetAsync(Url, _ => throw new InvalidOperationException("kaputt"));
        var result = await _cache.GetAsync(Url, default);

        Assert.Contains("alt@x", result.Ics);
        Assert.Equal("Abruf fehlgeschlagen", result.Error);
        Assert.True(result.Stale);
    }

    [Fact]
    public async Task Get_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        _fetcher.SetAsync(Url, async ct =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _cache.GetAsync(Url, cts.Token));
    }

    [Fact]
    public async Task Get_ReportsRealAttemptAndSuccessTimes()
    {
        _fetcher.Set(Url, Calendar(Event("a@x")));
        var fetchTime = _time.GetUtcNow();
        await _cache.GetAsync(Url, default);

        _time.Advance(TimeSpan.FromMinutes(2));
        var hit = await _cache.GetAsync(Url, default);
        Assert.Equal(fetchTime, hit.AttemptedAt);
        Assert.Equal(fetchTime, hit.SucceededAt);

        _time.Advance(TimeSpan.FromMinutes(4));
        var failTime = _time.GetUtcNow();
        _fetcher.Fail(Url, "HTTP 503");
        var failed = await _cache.GetAsync(Url, default);
        Assert.Equal(failTime, failed.AttemptedAt);
        Assert.Equal(fetchTime, failed.SucceededAt);
    }

    [Theory]
    [InlineData("\uFEFF\r\n  \r\nBEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n")]
    [InlineData("  \n  BEGIN:VCALENDAR\nEND:VCALENDAR\n")]
    public async Task Get_AcceptsCalendarWithLeadingBomOrWhitespace(string ics)
    {
        _fetcher.Set(Url, ics);

        var result = await _cache.GetAsync(Url, default);

        Assert.Null(result.Error);
        Assert.Equal(ics, result.Ics);
    }

    [Fact]
    public async Task Get_FailureAfterTwelveHours_StillServesStaleContent()
    {
        _fetcher.Set(Url, Calendar(Event("a@x")));
        await _cache.GetAsync(Url, default);

        _time.Advance(TimeSpan.FromHours(12));
        _fetcher.Fail(Url);
        var result = await _cache.GetAsync(Url, default);

        Assert.Contains("a@x", result.Ics);
        Assert.True(result.Stale);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Evicts_EntriesIdleLongerThanWindow()
    {
        const string other = "https://example.com/b.ics";
        _fetcher.Set(Url, Calendar(Event("a@x")));
        _fetcher.Set(other, Calendar(Event("b@x")));
        await _cache.GetAsync(Url, default);
        Assert.Equal(1, _cache.EntryCount);

        _time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        await _cache.GetAsync(other, default);

        Assert.Equal(1, _cache.EntryCount);
        await _cache.GetAsync(Url, default);
        Assert.Equal(2, _fetcher.CallCount(Url));
    }

    [Fact]
    public async Task Evict_KeepsEntryAccessedWithinWindow()
    {
        const string other = "https://example.com/b.ics";
        _fetcher.Set(Url, Calendar(Event("a@x")));
        _fetcher.Set(other, Calendar(Event("b@x")));
        await _cache.GetAsync(Url, default);

        _time.Advance(TimeSpan.FromDays(4));
        await _cache.GetAsync(Url, default);
        _time.Advance(TimeSpan.FromDays(4));
        await _cache.GetAsync(other, default);

        Assert.Equal(2, _cache.EntryCount);
    }

    [Fact]
    public async Task Evict_KeepsEntryWhoseGateIsHeld()
    {
        const string other = "https://example.com/b.ics";
        var release = new TaskCompletionSource();
        _fetcher.SetAsync(Url, async _ =>
        {
            await release.Task;
            return Calendar(Event("a@x"));
        });
        _fetcher.Set(other, Calendar(Event("b@x")));
        var pending = _cache.GetAsync(Url, default);
        await Task.Delay(50);

        _time.Advance(TimeSpan.FromDays(8));
        await _cache.GetAsync(other, default);

        Assert.Equal(2, _cache.EntryCount);
        release.SetResult();
        Assert.Contains("a@x", (await pending).Ics);
    }
}
