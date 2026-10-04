using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Webionic.ICalMerger.Calendars;
using Webionic.ICalMerger.Data;

namespace Webionic.ICalMerger.Users;

public sealed record UserRow(string Id, string Email, bool IsAdmin, bool IsLockedOut, bool HasPassword, int CalendarCount);

/// <summary>
/// Nutzerverwaltung für Admins. Jede Operation läuft in einem eigenen DI-Scope, damit sie
/// in langlebigen Blazor-Circuits keinen veralteten DbContext verwendet.
/// </summary>
public sealed class UserAdminService(IServiceScopeFactory scopes)
{
    public async Task<List<UserRow>> ListAsync()
    {
        using var scope = scopes.CreateScope();
        var users = UserManagerOf(scope);

        var adminIds = (await users.GetUsersInRoleAsync(Roles.Admin)).Select(u => u.Id).ToHashSet();
        var all = await users.Users.OrderBy(u => u.Email).ToListAsync();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var calendarCounts = await db.Calendars
            .GroupBy(c => c.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.OwnerId, x => x.Count);

        var rows = new List<UserRow>();
        foreach (var user in all)
        {
            rows.Add(new UserRow(
                user.Id,
                user.Email ?? user.UserName ?? "",
                adminIds.Contains(user.Id),
                await users.IsLockedOutAsync(user),
                user.PasswordHash is not null,
                calendarCounts.GetValueOrDefault(user.Id)));
        }
        return rows;
    }

    /// <returns>Der Einladungslink, den der Admin selbst weitergibt.</returns>
    public async Task<string> InviteAsync(string email, string baseUri)
    {
        email = (email ?? "").Trim();
        if (email.Length == 0 || !new EmailAddressAttribute().IsValid(email))
        {
            throw new DomainException("Das ist keine gültige E-Mail-Adresse.");
        }

        using var scope = scopes.CreateScope();
        var users = UserManagerOf(scope);

        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var created = await users.CreateAsync(user);
        if (!created.Succeeded)
        {
            throw new DomainException(created.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail")
                ? "Diese E-Mail-Adresse ist bereits vergeben."
                : string.Join(" ", created.Errors.Select(e => e.Description)));
        }

        return await BuildLinkAsync(users, user, baseUri);
    }

    public async Task<string> CreateResetLinkAsync(string userId, string baseUri)
    {
        using var scope = scopes.CreateScope();
        var users = UserManagerOf(scope);
        return await BuildLinkAsync(users, await FindAsync(users, userId), baseUri);
    }

    public async Task SetLockedAsync(string userId, bool locked)
    {
        using var scope = scopes.CreateScope();
        var users = UserManagerOf(scope);
        var user = await FindAsync(users, userId);

        if (locked)
        {
            await EnsureNotLastActiveAdminAsync(users, user);
            Check(await users.SetLockoutEnabledAsync(user, true));
            Check(await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue));
        }
        else
        {
            Check(await users.SetLockoutEndDateAsync(user, null));
            Check(await users.ResetAccessFailedCountAsync(user));
        }

        Check(await users.UpdateSecurityStampAsync(user));
    }

    public async Task SetAdminAsync(string userId, bool isAdmin)
    {
        using var scope = scopes.CreateScope();
        var users = UserManagerOf(scope);
        var user = await FindAsync(users, userId);

        if (isAdmin)
        {
            if (!await users.IsInRoleAsync(user, Roles.Admin)) Check(await users.AddToRoleAsync(user, Roles.Admin));
        }
        else
        {
            await EnsureNotLastActiveAdminAsync(users, user);
            if (await users.IsInRoleAsync(user, Roles.Admin)) Check(await users.RemoveFromRoleAsync(user, Roles.Admin));
        }

        Check(await users.UpdateSecurityStampAsync(user));
    }

    public async Task DeleteAsync(string userId, string actingUserId)
    {
        if (userId == actingUserId)
        {
            throw new DomainException("Du kannst dich nicht selbst löschen.");
        }

        using var scope = scopes.CreateScope();
        var users = UserManagerOf(scope);
        var user = await FindAsync(users, userId);

        await EnsureNotLastActiveAdminAsync(users, user);
        Check(await users.DeleteAsync(user));
    }

    private static UserManager<ApplicationUser> UserManagerOf(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    private static async Task<ApplicationUser> FindAsync(UserManager<ApplicationUser> users, string userId) =>
        await users.FindByIdAsync(userId) ?? throw new DomainException("Nutzer nicht gefunden.");

    private static async Task<string> BuildLinkAsync(UserManager<ApplicationUser> users, ApplicationUser user, string baseUri)
    {
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        return $"{baseUri.TrimEnd('/')}/Account/ResetPassword?code={code}";
    }

    private static async Task EnsureNotLastActiveAdminAsync(UserManager<ApplicationUser> users, ApplicationUser user)
    {
        if (!await users.IsInRoleAsync(user, Roles.Admin) || await users.IsLockedOutAsync(user)) return;

        var active = 0;
        foreach (var admin in await users.GetUsersInRoleAsync(Roles.Admin))
        {
            if (!await users.IsLockedOutAsync(admin)) active++;
        }

        if (active <= 1)
        {
            throw new DomainException("Der letzte aktive Admin kann nicht gesperrt, gelöscht oder herabgestuft werden.");
        }
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new DomainException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }
}
