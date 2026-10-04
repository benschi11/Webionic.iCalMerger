using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Webionic.ICalMerger.Data;

namespace Webionic.ICalMerger.Tests.Data;

public class MigrationsTests
{
    [Fact]
    public async Task Migrate_CreatesSchemaThatMatchesModel()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        // Wie in Program.cs: Identity-Schemaversion 3 über die IdentityOptions des Service-Providers.
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .UseApplicationServiceProvider(DesignTimeDbContextFactory.IdentitySchemaServices())
            .Options;
        await using var db = new ApplicationDbContext(options);

        await db.Database.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges(), "Modell und Migrationen weichen ab: neue Migration erzeugen.");
        Assert.Equal(0, await db.Calendars.CountAsync());
        Assert.Equal(0, await db.Sources.CountAsync());
    }
}
