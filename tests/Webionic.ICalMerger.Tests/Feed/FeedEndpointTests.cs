using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Webionic.ICalMerger.Calendars;
using Webionic.ICalMerger.Tests.Support;
using static Webionic.ICalMerger.Tests.Support.IcsFixtures;

namespace Webionic.ICalMerger.Tests.Feed;

public sealed class FeedEndpointTests : IDisposable
{
    private const string UrlA = "https://example.com/a.ics";
    private const string UrlB = "https://example.com/b.ics";

    private readonly AppFactory _factory = new();

    public FeedEndpointTests()
    {
        _factory.Fetcher.Set(UrlA, Calendar(Event("a@x", "Von A")));
        _factory.Fetcher.Set(UrlB, Calendar(Event("b@x", "Von B")));
    }

    public void Dispose() => _factory.Dispose();

    private static string FeedPath(string token) => $"/feed/{token}.ics";

    [Fact]
    public async Task Feed_ReturnsMergedCalendarWithHeaders()
    {
        var calendar = await _factory.SeedCalendarAsync("Familie", UrlA, UrlB);

        var response = await _factory.CreateClient().GetAsync(FeedPath(calendar.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("public, max-age=300", response.Headers.CacheControl!.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", body);
        Assert.Contains("UID:a@x", body);
        Assert.Contains("UID:b@x", body);
        Assert.Contains("X-WR-CALNAME:Familie", body);
    }

    [Fact]
    public async Task Feed_UnknownToken_Returns404()
    {
        var response = await _factory.CreateClient().GetAsync(FeedPath(TokenGenerator.NewToken()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Feed_UnknownToken_IsNotCachedAndHasNoHtmlBody()
    {
        var response = await _factory.CreateClient().GetAsync(FeedPath(TokenGenerator.NewToken()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.DoesNotContain("html", response.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("x")]
    [InlineData("..")]
    [InlineData("%2e%2e%2fetc%2fpasswd")]
    [InlineData("a%20b")]
    [InlineData("%27%20OR%201%3D1--")]
    public async Task Feed_GarbageToken_Returns404(string token)
    {
        var response = await _factory.CreateClient().GetAsync(FeedPath(token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Feed_EmptyOrHugeToken_Returns404()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/feed/.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(FeedPath(new string('a', 5000)))).StatusCode);
    }

    [Fact]
    public async Task Feed_HugeAndNonAsciiTokens_Return404()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(FeedPath(new string('a', 10_000)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(FeedPath("k%C3%A4se%C3%BC%E2%82%AC" + new string('a', 20)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/feed/../" + new string('a', 40) + ".ics")).StatusCode);
    }

    [Fact]
    public async Task Feed_TokenWithWrongCase_Returns404()
    {
        var calendar = await _factory.SeedCalendarAsync("K", UrlA);
        var swapped = new string(calendar.Token.Select(c => char.IsLower(c) ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)).ToArray());
        Assert.NotEqual(calendar.Token, swapped);

        var response = await _factory.CreateClient().GetAsync(FeedPath(swapped));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Feed_AllSourcesDown_Returns503()
    {
        var calendar = await _factory.SeedCalendarAsync("K", UrlA, UrlB);
        _factory.Fetcher.Fail(UrlA);
        _factory.Fetcher.Fail(UrlB);

        var response = await _factory.CreateClient().GetAsync(FeedPath(calendar.Token));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("60", response.Headers.GetValues("Retry-After").Single());
        Assert.DoesNotContain("html", response.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Feed_PartialFailure_Returns200WithRemainingSources()
    {
        var calendar = await _factory.SeedCalendarAsync("K", UrlA, UrlB);
        _factory.Fetcher.Fail(UrlB);

        var response = await _factory.CreateClient().GetAsync(FeedPath(calendar.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("UID:a@x", body);
        Assert.DoesNotContain("UID:b@x", body);
    }

    [Fact]
    public async Task Feed_WithoutSources_ReturnsEmptyValidCalendar()
    {
        var calendar = await _factory.SeedCalendarAsync("Leer");

        var response = await _factory.CreateClient().GetAsync(FeedPath(calendar.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("BEGIN:VCALENDAR", body);
        Assert.DoesNotContain("BEGIN:VEVENT", body);
    }

    [Fact]
    public async Task Feed_AfterTokenRegeneration_OldUrlIsGone()
    {
        var calendar = await _factory.SeedCalendarAsync("K", UrlA);
        string newToken;
        using (var scope = _factory.Services.CreateScope())
        {
            newToken = await scope.ServiceProvider.GetRequiredService<CalendarService>()
                .RegenerateTokenAsync(calendar.OwnerId, calendar.Id);
        }
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(FeedPath(calendar.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(FeedPath(newToken))).StatusCode);
    }

    [Fact]
    public async Task Feed_SourceServedStaleAfterError_StillReturns200()
    {
        var calendar = await _factory.SeedCalendarAsync("K", UrlA, UrlB);
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(FeedPath(calendar.Token))).StatusCode);

        _factory.Fetcher.Fail(UrlA);
        _factory.Time.Advance(TimeSpan.FromMinutes(10));
        var response = await client.GetAsync(FeedPath(calendar.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("UID:a@x", body);
        Assert.Contains("UID:b@x", body);
    }

    [Fact]
    public async Task Feed_IsAnonymousAndSetsNoCookies()
    {
        var calendar = await _factory.SeedCalendarAsync("K", UrlA);
        var client = _factory.CreateClient(new() { HandleCookies = false });

        var response = await client.GetAsync(FeedPath(calendar.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Feed_HeadRequest_ReturnsHeadersWithoutBody()
    {
        var calendar = await _factory.SeedCalendarAsync("K", UrlA);

        var response = await _factory.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Head, FeedPath(calendar.Token)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }
}
