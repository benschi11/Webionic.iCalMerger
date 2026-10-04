using System.Net;
using Webionic.ICalMerger.Tests.Support;

namespace Webionic.ICalMerger.Tests.Web;

public sealed class PipelineTests : IDisposable
{
    private readonly AppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task AnonymousRequest_BehindTlsProxy_RedirectsToLoginWithForwardedSchemeAndHost()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "kalender.example.org");

        var response = await _factory.NewClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://kalender.example.org/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task LoginPage_CarriesSecurityHeaders()
    {
        var response = await _factory.NewClient().GetAsync("/Account/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Referrer-Policy").Single());
    }
}
