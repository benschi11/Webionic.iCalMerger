using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace Webionic.ICalMerger.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite("Data Source=data/design.db")
            .UseApplicationServiceProvider(IdentitySchemaServices())
            .Options);

    /// <summary>
    /// IdentityDbContext liest die Schemaversion aus den IdentityOptions des Anwendungs-Service-Providers.
    /// Ohne DI (dotnet ef, Tests) muss sie wie in Program.cs gesetzt werden, sonst entsteht ein anderes Modell
    /// (ohne Passkey-Tabelle) und die Migration würde Tabellen löschen.
    /// </summary>
    public static IServiceProvider IdentitySchemaServices() =>
        new ServiceCollection()
            .Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .BuildServiceProvider();
}
