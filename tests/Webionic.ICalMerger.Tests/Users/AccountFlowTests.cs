using System.Net;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Webionic.ICalMerger.Data;
using Webionic.ICalMerger.Tests.Support;
using Webionic.ICalMerger.Users;

namespace Webionic.ICalMerger.Tests.Users;

public sealed class AccountFlowTests : IDisposable
{
    private readonly AppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static bool SetsLoginCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(c => c.StartsWith(".AspNetCore.Identity.Application", StringComparison.Ordinal));

    [Fact]
    public async Task LoginPage_IsGerman()
    {
        var response = await _factory.NewClient().GetAsync("/Account/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Anmelden", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/Account/Register")]
    [InlineData("/Account/ForgotPassword")]
    [InlineData("/Account/ExternalLogin")]
    [InlineData("/Account/Manage/ExternalLogins")]
    public async Task SelfServicePages_DoNotExist(string path)
    {
        var response = await _factory.NewClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Home_WithoutLogin_RedirectsToLogin()
    {
        var response = await _factory.NewClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Bootstrap_CreatesAdminWithAdminRole()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var admin = await users.FindByEmailAsync(AppFactory.AdminEmail);

        Assert.NotNull(admin);
        Assert.True(await users.IsInRoleAsync(admin, Roles.Admin));
    }

    [Fact]
    public async Task Login_WithWrongPassword_ShowsGermanError()
    {
        var response = await WebHelpers.LoginAsync(_factory.NewClient(), AppFactory.AdminEmail, "falsches passwort!");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("falsch", await response.Content.ReadAsStringAsync());
        Assert.False(SetsLoginCookie(response));
    }

    [Fact]
    public async Task Login_WithBootstrapAdmin_SetsCookieAndOpensHome()
    {
        var client = _factory.NewClient();

        var login = await WebHelpers.LoginAsync(client, AppFactory.AdminEmail, AppFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.Found, login.StatusCode);
        Assert.True(SetsLoginCookie(login));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Login_LocksAccountAfterFiveWrongAttempts()
    {
        var client = _factory.NewClient();
        for (var i = 0; i < 5; i++)
        {
            await WebHelpers.LoginAsync(client, AppFactory.AdminEmail, "falsches passwort!");
        }

        var response = await WebHelpers.LoginAsync(client, AppFactory.AdminEmail, AppFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("Lockout", response.Headers.Location!.ToString());
        Assert.False(SetsLoginCookie(response));
    }

    [Fact]
    public async Task InviteLink_LetsNewUserSetPasswordAndLogIn()
    {
        var admin = _factory.Services.GetRequiredService<UserAdminService>();
        var link = await admin.InviteAsync("neu@example.com", "http://localhost/");
        var pathAndQuery = new Uri(link).PathAndQuery;
        var code = pathAndQuery[(pathAndQuery.IndexOf("code=", StringComparison.Ordinal) + 5)..];
        var token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        var client = _factory.NewClient();

        var page = await client.GetAsync(pathAndQuery);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Passwort festlegen", await page.Content.ReadAsStringAsync());

        var post = await WebHelpers.PostFormAsync(client, pathAndQuery, "reset-password", new Dictionary<string, string>
        {
            ["Input.Email"] = "neu@example.com",
            ["Input.Password"] = "ein langes passwort",
            ["Input.ConfirmPassword"] = "ein langes passwort",
            ["Input.Code"] = token,
        });
        Assert.Equal(HttpStatusCode.Found, post.StatusCode);
        Assert.Contains("ResetPasswordConfirmation", post.Headers.Location!.ToString());

        var login = await WebHelpers.LoginAsync(_factory.NewClient(), "neu@example.com", "ein langes passwort");
        Assert.Equal(HttpStatusCode.Found, login.StatusCode);
        Assert.True(SetsLoginCookie(login));
    }

    [Fact]
    public async Task ResetLink_WithGarbageCode_RedirectsToInvalidPage()
    {
        var response = await _factory.NewClient().GetAsync("/Account/ResetPassword?code=%25%25kein-base64");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("InvalidPasswordReset", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task ResetLink_WithoutCode_RedirectsToInvalidPage()
    {
        var response = await _factory.NewClient().GetAsync("/Account/ResetPassword");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("InvalidPasswordReset", response.Headers.Location!.ToString());
    }
}
