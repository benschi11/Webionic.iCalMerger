using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Webionic.ICalMerger;
using Webionic.ICalMerger.Components;
using Webionic.ICalMerger.Components.Account;
using Webionic.ICalMerger.Data;
using Webionic.ICalMerger.Feed;
using Webionic.ICalMerger.Users;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

// Die Konfiguration wird erst beim Auflösen gelesen, damit Tests sie überschreiben können.
builder.Services.AddDbContextFactory<ApplicationDbContext>((sp, options) =>
    options.UseSqlite(sp.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.")));
// Identity braucht einen gewöhnlichen, scoped DbContext.
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());

builder.Services.AddAppIdentity();
builder.Services.AddSingleton<UserAdminService>();

var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    builder.Services.AddDataProtection()
        .SetApplicationName("iCalMerger")
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // Die App läuft im Container hinter dem Dokploy-Proxy und ist nur darüber erreichbar.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddCalendarServices(builder.Configuration);

var app = builder.Build();

// Ungültiges App:PublicBaseUrl soll den Start mit klarer Meldung abbrechen, nicht erst den ersten Einladungslink.
_ = UserAdminService.ReadPublicBaseUrl(app.Configuration);

// Datenbank-Verzeichnis anlegen und Migrationen anwenden.
var dataSource = new SqliteConnectionStringBuilder(app.Configuration.GetConnectionString("DefaultConnection")).DataSource;
if (!string.IsNullOrEmpty(dataSource) && Path.GetDirectoryName(Path.GetFullPath(dataSource)) is { } dataDirectory)
{
    Directory.CreateDirectory(dataDirectory);
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}

await AdminBootstrapper.EnsureAdminAsync(app.Services, app.Configuration);

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Explizit nach den Forwarded Headers, sonst laufen Authentifizierung und Challenge-Weiterleitungen mit dem Proxy-Schema (http).
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapFeedEndpoint();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();

app.Run();

public partial class Program;
