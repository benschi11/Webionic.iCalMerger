using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Webionic.ICalMerger.Tests.Support;
using Webionic.ICalMerger.Users;

namespace Webionic.ICalMerger.Tests.Users;

public sealed class AdminPageAccessTests : IDisposable
{
    private readonly AppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task AdminPage_WithoutLogin_RedirectsToLogin()
    {
        var response = await _factory.NewClient().GetAsync("/admin/users");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task AdminPage_ForAdmin_ShowsAdminOnlyContent()
    {
        var client = _factory.NewClient();
        await WebHelpers.LoginAsync(client, AppFactory.AdminEmail, AppFactory.AdminPassword);

        var response = await client.GetAsync("/admin/users");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Die Seite selbst ist nicht vorgerendert (Interactive Server). Der Router lässt den Admin durch, und
        // der Admin-Link im Kopf erscheint nur für die Rolle Admin.
        Assert.DoesNotContain("Kein Zugriff", html);
        Assert.Contains("href=\"admin/users\"", html);
    }

    [Fact]
    public async Task AdminPage_ForNormalUser_ShowsNoAccessAndNoAdminContent()
    {
        await _factory.CreateUserAsync("user@example.com", "ein langes passwort");
        var client = _factory.NewClient();
        await WebHelpers.LoginAsync(client, "user@example.com", "ein langes passwort");

        // Die Endpunkt-Autorisierung leitet angemeldete Nutzer ohne Rolle auf die deutsche Seite "Kein Zugriff" um.
        var response = await client.GetAsync("/admin/users");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("Account/AccessDenied", response.Headers.Location!.ToString());

        var html = await client.GetStringAsync(response.Headers.Location);
        Assert.Contains("Kein Zugriff", html);
        Assert.DoesNotContain("href=\"admin/users\"", html);
    }
}
