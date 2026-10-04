using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Webionic.ICalMerger.Calendars;
using Webionic.ICalMerger.Data;
using Webionic.ICalMerger.Fetching;

namespace Webionic.ICalMerger.Tests.Support;

/// <summary>Startet die echte App mit eigener SQLite-Datei und Fake-Fetcher.</summary>
internal sealed class AppFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@example.com";
    public const string AdminPassword = "correct horse battery";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"icalmerger-{Guid.NewGuid():N}.db");

    public FakeFetcher Fetcher { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = $"Data Source={_dbPath}",
            ["ADMIN_EMAIL"] = AdminEmail,
            ["ADMIN_PASSWORD"] = AdminPassword,
        }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICalendarFetcher>();
            services.AddSingleton<ICalendarFetcher>(Fetcher);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        });
    }

    public async Task<MergedCalendar> SeedCalendarAsync(string name, params string[] sourceUrls)
    {
        using var scope = Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        string ownerId;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var email = $"{Guid.NewGuid():N}@example.com";
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
            ownerId = user.Id;
        }

        var calendars = scope.ServiceProvider.GetRequiredService<CalendarService>();
        var calendar = await calendars.CreateAsync(ownerId, name);
        foreach (var url in sourceUrls)
        {
            await calendars.AddSourceAsync(ownerId, calendar.Id, url, url);
        }
        return (await calendars.GetAsync(ownerId, calendar.Id))!;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); } catch (IOException) { /* Aufräumen ist best effort */ }
        }
    }
}
