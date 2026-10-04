using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Webionic.ICalMerger.Data;

namespace Webionic.ICalMerger.Tests.Support;

/// <summary>SQLite-In-Memory-Datenbank, die als <see cref="IDbContextFactory{TContext}"/> dient.</summary>
internal sealed class TestDb : IDbContextFactory<ApplicationDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public TestDb()
    {
        _connection.Open();
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public ApplicationDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            // Dieselbe Identity-Schemaversion wie in der App. EF cacht das Modell pro Kontexttyp, es darf nicht variieren.
            .UseApplicationServiceProvider(DesignTimeDbContextFactory.IdentitySchemaServices())
            .Options);

    public async Task<string> AddUserAsync(string email)
    {
        await using var db = CreateDbContext();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    public void Dispose() => _connection.Dispose();
}
