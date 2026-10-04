using Microsoft.AspNetCore.Identity;
using Webionic.ICalMerger.Data;

namespace Webionic.ICalMerger.Users;

public static class IdentityRegistration
{
    /// <summary>Identity-Konfiguration der App. Tests verwenden dieselbe Methode wie Program.cs.</summary>
    public static IServiceCollection AddAppIdentity(this IServiceCollection services)
    {
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false;
                options.User.RequireUniqueEmail = true;
                // Der Benutzername ist die bereits geprüfte E-Mail-Adresse, Zeichenbeschränkungen würden z. B. o'neil@... ablehnen.
                options.User.AllowedUserNameCharacters = "";
                options.Password.RequiredLength = 10;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager()
            .AddErrorDescriber<GermanIdentityErrorDescriber>()
            .AddDefaultTokenProviders();

        // Einladungs- und Reset-Links sind 7 Tage gültig.
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromDays(7));

        // Sperren und Rollenwechsel sollen zeitnah greifen.
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

        return services;
    }
}
