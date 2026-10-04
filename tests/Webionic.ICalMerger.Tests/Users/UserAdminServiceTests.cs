using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Webionic.ICalMerger.Calendars;
using Webionic.ICalMerger.Data;
using Webionic.ICalMerger.Users;

namespace Webionic.ICalMerger.Tests.Users;

public sealed class UserAdminServiceTests : IDisposable
{
    private const string Password = "correct horse battery";
    private const string BaseUri = "https://cal.example.com/";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _services;
    private readonly UserAdminService _admin;

    public UserAdminServiceTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDataProtection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
        services.AddAppIdentity();
        services.AddSingleton<UserAdminService>();
        _services = services.BuildServiceProvider();

        using (var scope = _services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
        }

        _admin = _services.GetRequiredService<UserAdminService>();
    }

    private string? _actorId;

    /// <summary>Ein aktiver Admin, der die Operationen ausführt (wird beim ersten Zugriff angelegt).</summary>
    private string Actor => _actorId ??= SeedAsync("actor@example.com", admin: true).GetAwaiter().GetResult();

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private async Task<string> SeedAsync(string email, bool admin)
    {
        using var scope = _services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(Roles.Admin)) await roles.CreateAsync(new IdentityRole(Roles.Admin));

        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        if (admin) Assert.True((await users.AddToRoleAsync(user, Roles.Admin)).Succeeded);
        return user.Id;
    }

    private static string TokenFromLink(string link)
    {
        var code = link[(link.IndexOf("code=", StringComparison.Ordinal) + 5)..];
        return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
    }

    private async Task<T> WithUsersAsync<T>(Func<UserManager<ApplicationUser>, Task<T>> action)
    {
        using var scope = _services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }

    [Fact]
    public async Task Invite_CreatesUserWithoutPassword_AndLinkSetsPassword()
    {
        var link = await _admin.InviteAsync("  neu@example.com ", BaseUri, Actor);

        Assert.StartsWith("https://cal.example.com/Account/ResetPassword?code=", link);
        var token = TokenFromLink(link);

        var result = await WithUsersAsync(async users =>
        {
            var user = (await users.FindByEmailAsync("neu@example.com"))!;
            Assert.False(await users.HasPasswordAsync(user));
            var reset = await users.ResetPasswordAsync(user, token, "ein langes passwort");
            return (reset.Succeeded, await users.CheckPasswordAsync(user, "ein langes passwort"));
        });

        Assert.True(result.Item1);
        Assert.True(result.Item2);
    }

    [Theory]
    [InlineData("o'neil@example.com")]
    [InlineData("anna.müller@example.com")]
    public async Task Invite_AcceptsEmailsWithApostropheOrUmlaut(string email)
    {
        var link = await _admin.InviteAsync(email, BaseUri, Actor);

        Assert.StartsWith("https://cal.example.com/Account/ResetPassword?code=", link);
        Assert.NotNull(await WithUsersAsync(users => users.FindByEmailAsync(email)));
    }

    [Fact]
    public async Task Invite_LinkWorksOnlyOnce()
    {
        var token = TokenFromLink(await _admin.InviteAsync("neu@example.com", BaseUri, Actor));

        var second = await WithUsersAsync(async users =>
        {
            var user = (await users.FindByEmailAsync("neu@example.com"))!;
            Assert.True((await users.ResetPasswordAsync(user, token, "ein langes passwort")).Succeeded);
            return await users.ResetPasswordAsync(user, token, "ein anderes passwort");
        });

        Assert.False(second.Succeeded);
    }

    [Fact]
    public async Task Invite_RejectsDuplicateEmailIgnoringCase()
    {
        await _admin.InviteAsync("neu@example.com", BaseUri, Actor);

        var ex = await Assert.ThrowsAsync<DomainException>(() => _admin.InviteAsync("Neu@Example.com", BaseUri, Actor));

        Assert.Contains("bereits vergeben", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kein-at-zeichen")]
    public async Task Invite_RejectsInvalidEmail(string email)
    {
        await Assert.ThrowsAsync<DomainException>(() => _admin.InviteAsync(email, BaseUri, Actor));
    }

    [Fact]
    public async Task ResetLink_ReplacesExistingPassword()
    {
        var id = await SeedAsync("alt@example.com", admin: false);

        var token = TokenFromLink(await _admin.CreateResetLinkAsync(id, BaseUri, Actor));

        var ok = await WithUsersAsync(async users =>
        {
            var user = (await users.FindByIdAsync(id))!;
            Assert.True((await users.ResetPasswordAsync(user, token, "neues langes passwort")).Succeeded);
            return (await users.CheckPasswordAsync(user, Password), await users.CheckPasswordAsync(user, "neues langes passwort"));
        });

        Assert.False(ok.Item1);
        Assert.True(ok.Item2);
    }

    [Fact]
    public async Task ResetLink_ForUnknownUser_Throws()
    {
        await Assert.ThrowsAsync<DomainException>(() => _admin.CreateResetLinkAsync("gibt-es-nicht", BaseUri, Actor));
    }

    [Fact]
    public async Task Delete_Self_IsRejected()
    {
        var first = await SeedAsync("eins@example.com", admin: true);
        await SeedAsync("zwei@example.com", admin: true);

        await Assert.ThrowsAsync<DomainException>(() => _admin.DeleteAsync(first, actingUserId: first));
    }

    [Fact]
    public async Task List_ReportsCalendarCountPerUser()
    {
        var withCalendars = await SeedAsync("a@example.com", admin: false);
        await SeedAsync("b@example.com", admin: false);
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Calendars.AddRange(
                new MergedCalendar { OwnerId = withCalendars, Name = "Eins", Token = "token-eins" },
                new MergedCalendar { OwnerId = withCalendars, Name = "Zwei", Token = "token-zwei" });
            await db.SaveChangesAsync();
        }

        var rows = await _admin.ListAsync();

        Assert.Equal(2, rows.Single(r => r.Id == withCalendars).CalendarCount);
        Assert.Equal(0, rows.Single(r => r.Email == "b@example.com").CalendarCount);
    }

    [Fact]
    public async Task LastActiveAdmin_CannotBeDeletedLockedOrDemoted()
    {
        var onlyAdmin = await SeedAsync("admin@example.com", admin: true);
        await SeedAsync("user@example.com", admin: false);

        // Der Akteur muss selbst aktiver Admin sein, daher ist der letzte Admin nur über sich selbst erreichbar.
        var ex1 = await Assert.ThrowsAsync<DomainException>(() => _admin.SetLockedAsync(onlyAdmin, true, onlyAdmin));
        var ex2 = await Assert.ThrowsAsync<DomainException>(() => _admin.SetAdminAsync(onlyAdmin, false, onlyAdmin));
        Assert.Contains("letzte aktive Admin", ex1.Message);
        Assert.Contains("letzte aktive Admin", ex2.Message);

        var rows = await _admin.ListAsync();
        Assert.Contains(rows, r => r.Id == onlyAdmin && r.IsAdmin && !r.IsLockedOut);
    }

    [Fact]
    public async Task WithSecondAdmin_FirstCanBeDemotedAndDeleted()
    {
        var first = await SeedAsync("eins@example.com", admin: true);
        var second = await SeedAsync("zwei@example.com", admin: true);

        await _admin.SetAdminAsync(first, false, first);
        Assert.DoesNotContain(await _admin.ListAsync(), r => r.Id == first && r.IsAdmin);

        await _admin.DeleteAsync(first, actingUserId: second);
        Assert.DoesNotContain(await _admin.ListAsync(), r => r.Id == first);
    }

    [Fact]
    public async Task LockedAdmin_DoesNotCountAsActive()
    {
        var first = await SeedAsync("eins@example.com", admin: true);
        var second = await SeedAsync("zwei@example.com", admin: true);
        await _admin.SetLockedAsync(first, true, second);

        await Assert.ThrowsAsync<DomainException>(() => _admin.SetAdminAsync(second, false, second));
    }

    [Fact]
    public async Task SetLocked_LocksAndUnlocks()
    {
        var id = await SeedAsync("user@example.com", admin: false);

        await _admin.SetLockedAsync(id, true, Actor);
        Assert.True((await _admin.ListAsync()).Single(r => r.Id == id).IsLockedOut);

        await _admin.SetLockedAsync(id, false, Actor);
        Assert.False((await _admin.ListAsync()).Single(r => r.Id == id).IsLockedOut);
    }

    [Fact]
    public async Task Delete_RemovesUsersCalendarsViaCascade()
    {
        var owner = await SeedAsync("owner@example.com", admin: false);
        var actor = await SeedAsync("admin@example.com", admin: true);
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Calendars.Add(new MergedCalendar
            {
                OwnerId = owner,
                Name = "K",
                Token = TokenGenerator.NewToken(),
                CreatedAt = DateTime.UtcNow,
                Sources = [new CalendarSource { Name = "A", Url = "https://example.com/a.ics" }],
            });
            await db.SaveChangesAsync();
        }

        await _admin.DeleteAsync(owner, actingUserId: actor);

        using var check = _services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(0, await checkDb.Calendars.CountAsync());
        Assert.Equal(0, await checkDb.Sources.CountAsync());
    }

    [Fact]
    public async Task List_ShowsRoleLockAndPasswordStatus()
    {
        await SeedAsync("admin@example.com", admin: true);
        await _admin.InviteAsync("neu@example.com", BaseUri, Actor);

        var rows = await _admin.ListAsync();

        var admin = rows.Single(r => r.Email == "admin@example.com");
        var invited = rows.Single(r => r.Email == "neu@example.com");
        Assert.True(admin.IsAdmin);
        Assert.True(admin.HasPassword);
        Assert.False(invited.IsAdmin);
        Assert.False(invited.HasPassword);
    }

    [Fact]
    public async Task Mutations_RejectNonAdminActor()
    {
        var normal = await SeedAsync("normal@example.com", admin: false);
        var target = await SeedAsync("target@example.com", admin: false);

        await AssertNoPermissionAsync(
            () => _admin.InviteAsync("neu@example.com", BaseUri, normal),
            () => _admin.CreateResetLinkAsync(target, BaseUri, normal),
            () => _admin.SetLockedAsync(target, true, normal),
            () => _admin.SetAdminAsync(target, true, normal),
            () => _admin.DeleteAsync(target, normal),
            () => _admin.InviteAsync("neu@example.com", BaseUri, "gibt-es-nicht"));

        Assert.DoesNotContain(await _admin.ListAsync(), r => r.Email == "neu@example.com");
        Assert.DoesNotContain(await _admin.ListAsync(), r => r.Id == target && (r.IsAdmin || r.IsLockedOut));
    }

    [Fact]
    public async Task Mutations_RejectLockedAdminActor()
    {
        var locked = await SeedAsync("gesperrt@example.com", admin: true);
        var target = await SeedAsync("target@example.com", admin: false);
        await _admin.SetLockedAsync(locked, true, Actor);

        await AssertNoPermissionAsync(
            () => _admin.InviteAsync("neu@example.com", BaseUri, locked),
            () => _admin.CreateResetLinkAsync(target, BaseUri, locked),
            () => _admin.SetLockedAsync(target, true, locked),
            () => _admin.SetAdminAsync(target, true, locked),
            () => _admin.DeleteAsync(target, locked));
    }

    [Fact]
    public async Task Mutations_RejectDemotedAdminActor()
    {
        var demoted = await SeedAsync("herabgestuft@example.com", admin: true);
        var target = await SeedAsync("target@example.com", admin: false);
        await _admin.SetAdminAsync(demoted, false, Actor);

        await AssertNoPermissionAsync(
            () => _admin.InviteAsync("neu@example.com", BaseUri, demoted),
            () => _admin.CreateResetLinkAsync(target, BaseUri, demoted),
            () => _admin.SetLockedAsync(target, true, demoted),
            () => _admin.SetAdminAsync(target, true, demoted),
            () => _admin.DeleteAsync(target, demoted));
    }

    private static async Task AssertNoPermissionAsync(params Func<Task>[] actions)
    {
        foreach (var action in actions)
        {
            var ex = await Assert.ThrowsAsync<DomainException>(action);
            Assert.Equal("Keine Berechtigung.", ex.Message);
        }
    }

    [Theory]
    [InlineData("https://kalender.example.org/", "https://kalender.example.org")]
    [InlineData("https://kalender.example.org", "https://kalender.example.org")]
    [InlineData("http://localhost:8080//", "http://localhost:8080")]
    [InlineData("https://example.org/kalender/", "https://example.org/kalender")]
    public async Task PublicBaseUrl_WhenConfigured_OverridesRequestBase(string configured, string expectedBase)
    {
        var admin = AdminWithPublicBaseUrl(configured);

        var link = await admin.InviteAsync("neu@example.com", "http://evil.example/", Actor);

        Assert.StartsWith($"{expectedBase}/Account/ResetPassword?code=", link);
        Assert.StartsWith($"{expectedBase}/Account/ResetPassword?code=",
            await admin.CreateResetLinkAsync(Actor, "http://evil.example/", Actor));
    }

    [Fact]
    public async Task PublicBaseUrl_WhenUnset_UsesRequestBase()
    {
        var link = await _admin.InviteAsync("neu@example.com", "http://localhost:5000/", Actor);

        Assert.StartsWith("http://localhost:5000/Account/ResetPassword?code=", link);
    }

    [Theory]
    [InlineData("kalender.example.org")]
    [InlineData("ftp://kalender.example.org")]
    [InlineData("/relativ")]
    [InlineData("https://kalender.example.org/?x=1")]
    public void PublicBaseUrl_Invalid_FailsWithClearMessage(string configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AdminWithPublicBaseUrl(configured));

        Assert.Contains("App:PublicBaseUrl", ex.Message);
    }

    private UserAdminService AdminWithPublicBaseUrl(string value) => new(
        _services.GetRequiredService<IServiceScopeFactory>(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:PublicBaseUrl"] = value }).Build());

    [Fact]
    public void IdentityOptions_RequireTenCharactersAndSevenDayTokens()
    {
        var password = _services.GetRequiredService<IOptions<IdentityOptions>>().Value.Password;
        var tokens = _services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value;

        Assert.Equal(10, password.RequiredLength);
        Assert.Equal(TimeSpan.FromDays(7), tokens.TokenLifespan);
    }

    [Fact]
    public async Task ShortPassword_IsRejected()
    {
        using var scope = _services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var result = await users.CreateAsync(new ApplicationUser { UserName = "x@example.com", Email = "x@example.com" }, "kurz");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Bootstrap_CreatesAdminWhenNoUsersExist()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ADMIN_EMAIL"] = "boss@example.com",
            ["ADMIN_PASSWORD"] = Password,
        }).Build();

        await AdminBootstrapper.EnsureAdminAsync(_services, config);
        await AdminBootstrapper.EnsureAdminAsync(_services, config); // idempotent

        var row = Assert.Single(await _admin.ListAsync());
        Assert.Equal("boss@example.com", row.Email);
        Assert.True(row.IsAdmin);
    }

    [Fact]
    public async Task Bootstrap_WithoutConfigAndWithoutUsers_FailsWithClearMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AdminBootstrapper.EnsureAdminAsync(_services, new ConfigurationBuilder().Build()));

        Assert.Contains("ADMIN_EMAIL", ex.Message);
    }

    [Fact]
    public async Task Bootstrap_WithExistingUsers_NeedsNoConfig()
    {
        await SeedAsync("vorhanden@example.com", admin: true);

        await AdminBootstrapper.EnsureAdminAsync(_services, new ConfigurationBuilder().Build());

        Assert.Single(await _admin.ListAsync());
    }

    [Fact]
    public async Task Bootstrap_WhenRoleCannotBeCreated_FailsInGermanWithoutLeakingPassword()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(connection));
        services.AddAppIdentity();
        services.AddScoped<IRoleValidator<IdentityRole>, RejectingRoleValidator>();
        await using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ADMIN_EMAIL"] = "boss@example.com",
            ["ADMIN_PASSWORD"] = Password,
        }).Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AdminBootstrapper.EnsureAdminAsync(provider, config));

        Assert.Contains("Admin-Rolle", ex.Message);
        Assert.DoesNotContain(Password, ex.Message);
        using var check = provider.CreateScope();
        Assert.Empty(check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().Users);
    }

    private sealed class RejectingRoleValidator : IRoleValidator<IdentityRole>
    {
        public Task<IdentityResult> ValidateAsync(RoleManager<IdentityRole> manager, IdentityRole role) =>
            Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "Rejected", Description = "abgelehnt" }));
    }
}
