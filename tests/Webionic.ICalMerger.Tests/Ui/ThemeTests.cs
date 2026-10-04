using System.Net;
using Webionic.ICalMerger.Tests.Support;

namespace Webionic.ICalMerger.Tests.Ui;

public sealed class ThemeTests : IDisposable
{
    private readonly AppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task LoginPage_UsesWebionicThemeAndLogo()
    {
        var response = await _factory.NewClient().GetAsync("/Account/Login");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches(@"css/app(\.[A-Za-z0-9]+)?\.css", html);
        Assert.Contains("img/logo-new-light.svg", html);
        Assert.Contains("lang=\"de\"", html);
    }

    [Fact]
    public async Task Stylesheet_ContainsThemeTokensComponentsAndScannedUtilities()
    {
        var css = await _factory.NewClient().GetStringAsync("/css/app.css");

        Assert.Contains("--color-navy-900", css);   // @theme-Tokens
        Assert.Contains(".btn-primary", css);        // Komponentenklassen
        Assert.Contains(".hex", css);
        Assert.Contains(".max-w-md", css);           // Utility aus dem Markup der .razor-Dateien: @source greift
        Assert.DoesNotContain("--bs-", css);         // kein Bootstrap
    }

    [Theory]
    [InlineData("/css/app.css", "text/css")]
    [InlineData("/fonts/hanken-grotesk-latin-wght-normal.woff2", "font/woff2")]
    [InlineData("/img/logo-new-light.svg", "image/svg+xml")]
    public async Task ThemeAssets_AreServed(string path, string contentType)
    {
        var response = await _factory.NewClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
    }
}
