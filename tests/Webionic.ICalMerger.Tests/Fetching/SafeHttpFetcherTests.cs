using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Webionic.ICalMerger.Fetching;
using Webionic.ICalMerger.Tests.Support;

namespace Webionic.ICalMerger.Tests.Fetching;

public class SafeHttpFetcherTests
{
    private static SafeHttpFetcher Create(FetcherOptions? options = null, bool allowLoopback = true) =>
        new(new HttpClient(SafeHttpFetcher.CreateHandler(allowLoopback ? _ => true : null)), options);

    [Theory]
    [InlineData("webcal://example.com/a.ics", "https://example.com/a.ics")]
    [InlineData("WEBCAL://example.com/a.ics", "https://example.com/a.ics")]
    [InlineData("webcals://example.com/a.ics", "https://example.com/a.ics")]
    [InlineData("  https://example.com/a.ics  ", "https://example.com/a.ics")]
    [InlineData("http://example.com/a.ics", "http://example.com/a.ics")]
    public void NormalizeUrl_RewritesAndTrims(string input, string expected)
    {
        Assert.Equal(expected, SafeHttpFetcher.NormalizeUrl(input).ToString());
    }

    [Theory]
    [InlineData("ftp://example.com/a.ics")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("kein url")]
    [InlineData("")]
    public void NormalizeUrl_RejectsInvalidUrls(string input)
    {
        var ex = Assert.Throws<FetchException>(() => SafeHttpFetcher.NormalizeUrl(input));
        Assert.Equal("Ungültige URL", ex.Message);
    }

    [Fact]
    public async Task Fetch_ReturnsBody()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/cal.ics", () => Results.Text("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", "text/calendar")));

        var body = await Create().FetchAsync(server.BaseUrl + "/cal.ics", CancellationToken.None);

        Assert.StartsWith("BEGIN:VCALENDAR", body);
    }

    [Fact]
    public async Task Fetch_BlocksLoopbackByDefault()
    {
        await using var server = await LocalHttpServer.StartAsync(app => app.MapGet("/cal.ics", () => "x"));

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            Create(allowLoopback: false).FetchAsync(server.BaseUrl + "/cal.ics", CancellationToken.None));

        Assert.Contains("nicht erlaubt", ex.Message);
    }

    [Fact]
    public async Task Fetch_NonSuccessStatus_Throws()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/missing.ics", () => Results.NotFound()));

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            Create().FetchAsync(server.BaseUrl + "/missing.ics", CancellationToken.None));

        Assert.Equal("HTTP 404", ex.Message);
    }

    [Fact]
    public async Task Fetch_RejectsTooLargeResponseWithContentLength()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/big.ics", () => Results.Text(new string('x', 5000), "text/calendar")));

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            Create(new FetcherOptions(TimeSpan.FromSeconds(5), 1000)).FetchAsync(server.BaseUrl + "/big.ics", CancellationToken.None));

        Assert.Contains("zu groß", ex.Message);
    }

    [Fact]
    public async Task Fetch_RejectsTooLargeChunkedResponse()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/chunked.ics", async (HttpContext ctx) =>
            {
                for (var i = 0; i < 10; i++)
                {
                    await ctx.Response.WriteAsync(new string('x', 500));
                    await ctx.Response.Body.FlushAsync();
                }
            }));

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            Create(new FetcherOptions(TimeSpan.FromSeconds(5), 2000)).FetchAsync(server.BaseUrl + "/chunked.ics", CancellationToken.None));

        Assert.Contains("zu groß", ex.Message);
    }

    [Fact]
    public async Task Fetch_TimesOut()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/slow.ics", async (HttpContext ctx) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted);
                return "x";
            }));

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            Create(new FetcherOptions(TimeSpan.FromMilliseconds(300), 1000)).FetchAsync(server.BaseUrl + "/slow.ics", CancellationToken.None));

        Assert.Contains("Zeitüberschreitung", ex.Message);
    }

    [Fact]
    public async Task Fetch_FollowsRedirects()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
        {
            app.MapGet("/a", () => Results.Redirect("/b"));
            app.MapGet("/b", () => Results.Text("BEGIN:VCALENDAR\r\nEND:VCALENDAR", "text/calendar"));
        });

        var body = await Create().FetchAsync(server.BaseUrl + "/a", CancellationToken.None);

        Assert.StartsWith("BEGIN:VCALENDAR", body);
    }

    [Fact]
    public async Task Fetch_StopsOnRedirectLoop()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/loop", () => Results.Redirect("/loop")));

        await Assert.ThrowsAsync<FetchException>(() =>
            Create().FetchAsync(server.BaseUrl + "/loop", CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_CallerCancellation_IsNotReportedAsTimeout()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/slow.ics", async (HttpContext ctx) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted);
                return "x";
            }));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create().FetchAsync(server.BaseUrl + "/slow.ics", cts.Token));
    }

    [Fact]
    public async Task Fetch_BlocksRedirectToInternalAddress()
    {
        // Nur 127.0.0.1 ist erlaubt (der Teststart). Das Redirect-Ziel [::1] muss im ConnectCallback scheitern.
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/a", () => Results.Redirect("http://[::1]:1/secret")));
        var fetcher = new SafeHttpFetcher(new HttpClient(SafeHttpFetcher.CreateHandler(ip => ip.Equals(System.Net.IPAddress.Loopback))));

        var ex = await Assert.ThrowsAsync<FetchException>(() => fetcher.FetchAsync(server.BaseUrl + "/a", CancellationToken.None));

        Assert.Contains("nicht erlaubt", ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
    }

    [Fact]
    public async Task Fetch_BlocksHostnameResolvingToLoopback()
    {
        // "localhost" wird real aufgelöst (kein Internet nötig) und landet auf Loopback.
        await using var server = await LocalHttpServer.StartAsync(app => app.MapGet("/cal.ics", () => "x"));
        var port = new Uri(server.BaseUrl).Port;

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            Create(allowLoopback: false).FetchAsync($"http://localhost:{port}/cal.ics", CancellationToken.None));

        Assert.Contains("nicht erlaubt", ex.Message);
    }

    [Fact]
    public async Task Fetch_BodyReadFailsMidStream_ThrowsFetchExceptionWithoutUrl()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/broken.ics", async (HttpContext ctx) =>
            {
                ctx.Response.ContentLength = 100000;
                await ctx.Response.WriteAsync(new string('x', 100));
                await ctx.Response.Body.FlushAsync();
                ctx.Abort();
            }));
        var url = server.BaseUrl + "/broken.ics";

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            Create(new FetcherOptions(TimeSpan.FromSeconds(5), 1_000_000)).FetchAsync(url, CancellationToken.None));

        Assert.DoesNotContain(url, ex.Message);
        Assert.DoesNotContain("127.0.0.1", ex.Message);
    }
}
