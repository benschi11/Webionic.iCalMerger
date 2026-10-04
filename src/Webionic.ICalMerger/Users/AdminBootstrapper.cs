using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Webionic.ICalMerger.Data;

namespace Webionic.ICalMerger.Users;

public static class AdminBootstrapper
{
    /// <summary>Legt die Rolle Admin an und, wenn es noch keinen Nutzer gibt, den ersten Admin aus ADMIN_EMAIL und ADMIN_PASSWORD.</summary>
    public static async Task EnsureAdminAsync(IServiceProvider services, IConfiguration config)
    {
        using var scope = services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        if (!await roles.RoleExistsAsync(Roles.Admin))
        {
            var role = await roles.CreateAsync(new IdentityRole(Roles.Admin));
            if (!role.Succeeded)
            {
                throw new InvalidOperationException(
                    "Die Admin-Rolle konnte nicht angelegt werden: " + string.Join(" ", role.Errors.Select(e => e.Description)));
            }
        }

        if (await users.Users.AnyAsync())
        {
            return;
        }

        var email = config["ADMIN_EMAIL"];
        var password = config["ADMIN_PASSWORD"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "Es existiert noch kein Nutzer. Bitte ADMIN_EMAIL und ADMIN_PASSWORD setzen, um den ersten Admin anzulegen.");
        }

        var admin = new ApplicationUser { UserName = email.Trim(), Email = email.Trim(), EmailConfirmed = true };
        var created = await users.CreateAsync(admin, password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                "Der erste Admin konnte nicht angelegt werden: " + string.Join(" ", created.Errors.Select(e => e.Description)));
        }

        var inRole = await users.AddToRoleAsync(admin, Roles.Admin);
        if (!inRole.Succeeded)
        {
            // Kein halbfertiger Nutzer ohne Admin-Rolle: beim nächsten Start soll die Anlage erneut laufen.
            await users.DeleteAsync(admin);
            throw new InvalidOperationException(
                "Der erste Admin konnte der Admin-Rolle nicht zugewiesen werden: " + string.Join(" ", inRole.Errors.Select(e => e.Description)));
        }
    }
}
