# iCal Merger Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Blazor-Server-App, in der angemeldete Nutzer mehrere iCal-URLs zu Merge-Kalendern mit je eigener, token-geschützter Feed-URL zusammenfassen. Nutzerverwaltung über ASP.NET Core Identity, Betrieb als Docker-Container auf Dokploy.

**Architecture:** Eine ASP.NET-Core-App (.NET 10) mit Blazor Interactive Server, EF Core/SQLite und Identity. Der Merge ist eine reine, textbasierte Funktion (`IcsMerger`). Quellen werden über einen SSRF-sicheren `HttpClient` mit Cache (`SourceCache`) geholt. Der Feed ist ein anonymer Minimal-API-Endpunkt `GET /feed/{token}.ics`.

**Tech Stack:** .NET 10, Blazor Web App (Interactive Server), EF Core 10 + SQLite, ASP.NET Core Identity (mit Rollen), xUnit, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.TimeProvider.Testing`, Docker.

**Spec:** `docs/superpowers/specs/2026-10-04-ical-merger-design.md`

## Global Constraints

- Ziel-Framework `net10.0`, Nullable und ImplicitUsings an. Root-Namespace `Webionic.ICalMerger`. Solution-Datei im Repo-Root, Projekte unter `src/` und `tests/`.
- Blazor Web App mit Interactive Server. Seiten sind standardmäßig statisch (`--all-interactive false`), interaktive Seiten setzen `@rendermode @(new InteractiveServerRenderMode(prerender: false))`.
- Persistenz: EF Core + SQLite. Der Connection-String heißt `DefaultConnection` (Vorlagenname). Als Umgebungsvariable `ConnectionStrings__DefaultConnection`, in Docker `Data Source=/data/app.db`.
- Keine Selbstregistrierung, keine externen Logins, keine Passkeys, kein SMTP. Es gibt nur Passwort-Login. Nutzer werden vom Admin angelegt.
- Einladung und Passwort-Reset über Link `/{Basis}/Account/ResetPassword?code=<Base64Url(UTF8(Token))>`, gültig 7 Tage, nach Benutzung ungültig.
- Rolle `Admin`. Der letzte aktive Admin kann nicht gelöscht, gesperrt oder herabgestuft werden. Ein Admin kann sich nicht selbst löschen.
- Limits (Konfiguration `Limits:MaxCalendarsPerUser` = 10, `Limits:MaxSourcesPerCalendar` = 20).
- Quell-Abruf: nur http/https (`webcal://` und `webcals://` werden zu `https://`), Timeout 10 s, max. 10 MB, max. 5 Redirects. Die Ziel-IP wird beim Verbindungsaufbau geprüft. Geblockt sind Loopback, private, Link-Local, Unique-Local, Multicast und unspezifizierte Adressen.
- Cache pro Quell-URL 5 Minuten, Stale-on-Error, Fehler-Backoff 1 Minute, ein gleichzeitiger Abruf pro URL.
- Merge: nur `VEVENT` und `VTIMEZONE`. Dedupe-Schlüssel `UID` + `RECURRENCE-ID`, die kleinere `SortOrder` gewinnt. Events ohne `UID` werden immer übernommen. `VTIMEZONE` einmal pro `TZID`. Ausgabe mit CRLF und Faltung bei 75 Oktetten.
- Feed: unbekanntes Token `404`. Erfolg `200` mit `Content-Type: text/calendar; charset=utf-8` und `Cache-Control: public, max-age=300`. Alle Quellen ausgefallen (bei mindestens einer Quelle) `503`. Teilausfall liefert den Rest. Ein Kalender ohne Quellen liefert einen leeren, gültigen Kalender.
- Quell-URLs werden nie geloggt und in der UI gekürzt angezeigt (Host + Pfadanfang).
- Alle sichtbaren Texte auf Deutsch. HTTPS terminiert der Proxy, die App wertet `X-Forwarded-*` aus. Es gibt keine HTTPS-Weiterleitung in der App.
- Commit-Nachrichten enden mit der Zeile `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>` (zweites `-m`).
- Shell-Hinweis: Befehle laufen im Repo-Root `/home/benedikt/_DEV/Webionic.iCalMerger`.

## Review Focus

Eingabeklassen, die die Spec nicht ausdrücklich nennt und die im Alltag am ehesten Probleme machen. Jede Zeile hat einen Test im genannten Task.

1. Quelle liefert eine UTF-8-BOM, nur `\n` als Zeilenende oder riesige gefaltete Zeilen (Base64-`ATTACH`). Der Termin muss trotzdem sauber und schnell übernommen werden (Task 2).
2. Quelle antwortet mit `200`, aber mit HTML (Login-Seite, Captive Portal). Das darf nicht in den Kalender gemischt werden, sondern ist ein Fehler an der Quelle (Task 4 beim Hinzufügen, Task 5 im Cache).
3. Quelle ist abgeschnitten (`BEGIN:VEVENT` ohne `END:VEVENT`). Der kaputte Termin fällt weg, die übrigen Termine bleiben erhalten (Task 2).
4. Feed-URL mit Müll im Token (leer, extrem lang, `../`, falsche Groß-/Kleinschreibung). Es gibt `404`, nie `500` (Task 6).
5. Kalendername mit `,`, `;`, Backslash oder Zeilenumbruch. Er wird als iCal-TEXT escaped und bricht den Feed nicht (Task 2).

---

### Task 1: Solution, Vorlage zurechtstutzen, deutsche Account-Seiten

**Files:**
- Create: `Webionic.iCalMerger.slnx` (bzw. `.sln`, je nach SDK), `.gitignore`
- Create: `src/Webionic.ICalMerger/**` (aus Vorlage), `tests/Webionic.ICalMerger.Tests/**`
- Modify/Delete: siehe Schritte
- Test: Build + manueller Start

**Interfaces:**
- Produces: `ApplicationDbContext`, `ApplicationUser`, `IdentityRedirectManager`, `StatusMessage`, Pages `/Account/Login`, `/Account/ResetPassword`, `/Account/Manage/ChangePassword`, `POST /Account/Logout`, `public partial class Program` (für `WebApplicationFactory`).

- [ ] **Step 1: Projekte anlegen**

```bash
cd /home/benedikt/_DEV/Webionic.iCalMerger
dotnet new sln -n Webionic.iCalMerger
dotnet new blazor -n Webionic.ICalMerger -o src/Webionic.ICalMerger --auth Individual --interactivity Server --all-interactive false
dotnet new xunit -n Webionic.ICalMerger.Tests -o tests/Webionic.ICalMerger.Tests
dotnet sln add src/Webionic.ICalMerger tests/Webionic.ICalMerger.Tests
dotnet add tests/Webionic.ICalMerger.Tests reference src/Webionic.ICalMerger
dotnet new gitignore
printf '\n# App data\ndata/\n*.db\n*.db-shm\n*.db-wal\n' >> .gitignore
```

- [ ] **Step 2: Testprojekt-Pakete ergänzen**

Ersetze in `tests/Webionic.ICalMerger.Tests/Webionic.ICalMerger.Tests.csproj` die ItemGroups so, dass zusätzlich enthalten sind (bestehende xunit-/Test-SDK-Pakete der Vorlage bleiben):

```xml
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.*" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.*" />
  </ItemGroup>
```

Lösche die Vorlagendatei `tests/Webionic.ICalMerger.Tests/UnitTest1.cs`.

- [ ] **Step 3: Nicht benötigte Vorlagenteile löschen**

```bash
cd src/Webionic.ICalMerger
rm -f Data/app.db
rm -rf Components/Pages/Counter.razor Components/Pages/Weather.razor Components/Pages/Auth.razor
cd Components/Account
rm -f IdentityNoOpEmailSender.cs PasskeyInputModel.cs PasskeyOperation.cs
rm -rf Shared/ExternalLoginPicker.razor Shared/ManageLayout.razor Shared/ManageNavMenu.razor Shared/PasskeySubmit.razor Shared/PasskeySubmit.razor.js Shared/ShowRecoveryCodes.razor
rm -rf Pages/Manage
rm -f Pages/ConfirmEmail.razor Pages/ConfirmEmailChange.razor Pages/ExternalLogin.razor Pages/ForgotPassword.razor Pages/ForgotPasswordConfirmation.razor Pages/LoginWith2fa.razor Pages/LoginWithRecoveryCode.razor Pages/Register.razor Pages/RegisterConfirmation.razor Pages/ResendEmailConfirmation.razor
cd ../../..
```

Es bleiben unter `Components/Account/Pages`: `_Imports.razor`, `AccessDenied.razor`, `InvalidPasswordReset.razor`, `InvalidUser.razor`, `Lockout.razor`, `Login.razor`, `ResetPassword.razor`, `ResetPasswordConfirmation.razor`. Sie werden in den nächsten Schritten ersetzt.

- [ ] **Step 4: `Webionic.ICalMerger.csproj` bereinigen**

Ersetze den Inhalt durch:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UserSecretsId>aspnet-Webionic_ICalMerger-4baa4cf0-c4c1-4f80-bda6-477bb6f2ef73</UserSecretsId>
    <BlazorDisableThrowNavigationException>true</BlazorDisableThrowNavigationException>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.0.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.*" PrivateAssets="all" />
  </ItemGroup>

</Project>
```

(Falls die `UserSecretsId` der generierten Vorlage von obiger abweicht, behalte die generierte.)

- [ ] **Step 5: `appsettings.json`**

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=data/app.db;Default Timeout=30"
  },
  "Limits": {
    "MaxCalendarsPerUser": 10,
    "MaxSourcesPerCalendar": 20
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

- [ ] **Step 6: `Program.cs` ersetzen**

```csharp
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Webionic.ICalMerger.Components;
using Webionic.ICalMerger.Components.Account;
using Webionic.ICalMerger.Data;

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

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = true;
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
    .AddDefaultTokenProviders();

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

var app = builder.Build();

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

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();

app.Run();

public partial class Program;
```

Hinweis: Heißt `ForwardedHeadersOptions.KnownIPNetworks` im SDK anders (ältere Namen: `KnownNetworks`), nimm den Namen, den der Compiler kennt.

- [ ] **Step 7: `Components/Account/IdentityComponentsEndpointRouteBuilderExtensions.cs` ersetzen**

```csharp
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Webionic.ICalMerger.Data;

namespace Microsoft.AspNetCore.Routing;

internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    // Der einzige zusätzliche Endpunkt: Abmelden. Der Parameter aus dem Formular
    // erzwingt die Antiforgery-Prüfung.
    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var accountGroup = endpoints.MapGroup("/Account");

        accountGroup.MapPost("/Logout", async (
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromForm] string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            return TypedResults.LocalRedirect("~/Account/Login");
        });

        return accountGroup;
    }
}
```

- [ ] **Step 8: Kleine Anpassungen an Hilfsdateien**

In `Components/Account/Shared/StatusMessage.razor` ersetze `DisplayMessage.StartsWith("Error")` durch `DisplayMessage.StartsWith("Fehler")`.

In `Components/Account/IdentityRedirectManager.cs` ersetze den Text in `RedirectToInvalidUser` durch:

```csharp
=> RedirectToWithStatus("Account/InvalidUser", $"Fehler: Benutzer mit der ID '{userManager.GetUserId(context.User)}' konnte nicht geladen werden.", context);
```

- [ ] **Step 9: Account-Seiten auf Deutsch ersetzen**

`Components/Account/Pages/Login.razor`:

```razor
@page "/Account/Login"

@using System.ComponentModel.DataAnnotations
@using Microsoft.AspNetCore.Identity
@using Webionic.ICalMerger.Data

@inject SignInManager<ApplicationUser> SignInManager
@inject IdentityRedirectManager RedirectManager

<PageTitle>Anmelden</PageTitle>

<h1>Anmelden</h1>
<div class="row">
    <div class="col-lg-6">
        <StatusMessage Message="@errorMessage" />
        <EditForm Model="Input" method="post" OnValidSubmit="LoginUser" FormName="login">
            <DataAnnotationsValidator />
            <ValidationSummary class="text-danger" role="alert" />
            <div class="form-floating mb-3">
                <InputText @bind-Value="Input.Email" id="Input.Email" class="form-control" autocomplete="username" aria-required="true" placeholder="name@example.com" />
                <label for="Input.Email" class="form-label">E-Mail</label>
                <ValidationMessage For="() => Input.Email" class="text-danger" />
            </div>
            <div class="form-floating mb-3">
                <InputText type="password" @bind-Value="Input.Password" id="Input.Password" class="form-control" autocomplete="current-password" aria-required="true" placeholder="Passwort" />
                <label for="Input.Password" class="form-label">Passwort</label>
                <ValidationMessage For="() => Input.Password" class="text-danger" />
            </div>
            <div class="checkbox mb-3">
                <label class="form-label">
                    <InputCheckbox @bind-Value="Input.RememberMe" class="darker-border-checkbox form-check-input" />
                    Angemeldet bleiben
                </label>
            </div>
            <button type="submit" class="w-100 btn btn-lg btn-primary">Anmelden</button>
        </EditForm>
        <p class="text-muted mt-3">Passwort vergessen? Bitte melde dich beim Admin, er schickt dir einen neuen Link.</p>
    </div>
</div>

@code {
    private string? errorMessage;

    [SupplyParameterFromForm]
    private InputModel Input { get; set; } = default!;

    [SupplyParameterFromQuery]
    private string? ReturnUrl { get; set; }

    protected override void OnInitialized() => Input ??= new();

    private async Task LoginUser()
    {
        var result = await SignInManager.PasswordSignInAsync(Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            RedirectManager.RedirectTo(ReturnUrl);
        }
        else if (result.IsLockedOut)
        {
            RedirectManager.RedirectTo("Account/Lockout");
        }
        else
        {
            errorMessage = "Fehler: E-Mail oder Passwort ist falsch.";
        }
    }

    private sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte eine E-Mail-Adresse eingeben.")]
        [EmailAddress(ErrorMessage = "Das ist keine gültige E-Mail-Adresse.")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Bitte ein Passwort eingeben.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";

        public bool RememberMe { get; set; }
    }
}
```

`Components/Account/Pages/ResetPassword.razor`:

```razor
@page "/Account/ResetPassword"

@using System.ComponentModel.DataAnnotations
@using System.Text
@using Microsoft.AspNetCore.Identity
@using Microsoft.AspNetCore.WebUtilities
@using Webionic.ICalMerger.Data

@inject IdentityRedirectManager RedirectManager
@inject UserManager<ApplicationUser> UserManager

<PageTitle>Passwort festlegen</PageTitle>

<h1>Passwort festlegen</h1>
<p>Gib zur Bestätigung deine E-Mail-Adresse ein und wähle ein Passwort (mindestens 10 Zeichen).</p>
<hr />
<div class="row">
    <div class="col-md-6">
        <StatusMessage Message="@Message" />
        <EditForm Model="Input" FormName="reset-password" OnValidSubmit="OnValidSubmitAsync" method="post">
            <DataAnnotationsValidator />
            <ValidationSummary class="text-danger" role="alert" />

            <input type="hidden" name="Input.Code" value="@Input.Code" />
            <div class="form-floating mb-3">
                <InputText @bind-Value="Input.Email" id="Input.Email" class="form-control" autocomplete="username" aria-required="true" placeholder="name@example.com" />
                <label for="Input.Email" class="form-label">E-Mail</label>
                <ValidationMessage For="() => Input.Email" class="text-danger" />
            </div>
            <div class="form-floating mb-3">
                <InputText type="password" @bind-Value="Input.Password" id="Input.Password" class="form-control" autocomplete="new-password" aria-required="true" placeholder="Passwort" />
                <label for="Input.Password" class="form-label">Passwort</label>
                <ValidationMessage For="() => Input.Password" class="text-danger" />
            </div>
            <div class="form-floating mb-3">
                <InputText type="password" @bind-Value="Input.ConfirmPassword" id="Input.ConfirmPassword" class="form-control" autocomplete="new-password" aria-required="true" placeholder="Passwort wiederholen" />
                <label for="Input.ConfirmPassword" class="form-label">Passwort wiederholen</label>
                <ValidationMessage For="() => Input.ConfirmPassword" class="text-danger" />
            </div>
            <button type="submit" class="w-100 btn btn-lg btn-primary">Passwort speichern</button>
        </EditForm>
    </div>
</div>

@code {
    private IEnumerable<IdentityError>? identityErrors;

    [SupplyParameterFromForm]
    private InputModel Input { get; set; } = default!;

    [SupplyParameterFromQuery]
    private string? Code { get; set; }

    private string? Message => identityErrors is null ? null : $"Fehler: {string.Join(" ", identityErrors.Select(error => error.Description))}";

    protected override void OnInitialized()
    {
        Input ??= new();

        if (Code is null)
        {
            RedirectManager.RedirectTo("Account/InvalidPasswordReset");
            return;
        }

        try
        {
            Input.Code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(Code));
        }
        catch (FormatException)
        {
            RedirectManager.RedirectTo("Account/InvalidPasswordReset");
        }
    }

    private async Task OnValidSubmitAsync()
    {
        var user = await UserManager.FindByEmailAsync(Input.Email);
        if (user is null)
        {
            // Nicht verraten, dass es den Nutzer nicht gibt.
            RedirectManager.RedirectTo("Account/ResetPasswordConfirmation");
            return;
        }

        var result = await UserManager.ResetPasswordAsync(user, Input.Code, Input.Password);
        if (result.Succeeded)
        {
            RedirectManager.RedirectTo("Account/ResetPasswordConfirmation");
            return;
        }

        identityErrors = result.Errors.Select(e => e.Code == "InvalidToken"
            ? new IdentityError { Code = e.Code, Description = "Der Link ist ungültig oder abgelaufen." }
            : e);
    }

    private sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte eine E-Mail-Adresse eingeben.")]
        [EmailAddress(ErrorMessage = "Das ist keine gültige E-Mail-Adresse.")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Bitte ein Passwort eingeben.")]
        [StringLength(100, ErrorMessage = "Das Passwort muss mindestens {2} Zeichen lang sein.", MinimumLength = 10)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";

        [DataType(DataType.Password)]
        [Compare("Password", ErrorMessage = "Die Passwörter stimmen nicht überein.")]
        public string ConfirmPassword { get; set; } = "";

        [Required]
        public string Code { get; set; } = "";
    }
}
```

Kleine Seiten, jeweils eine Datei in `Components/Account/Pages/`:

`ResetPasswordConfirmation.razor`:

```razor
@page "/Account/ResetPasswordConfirmation"
<PageTitle>Passwort gespeichert</PageTitle>

<h1>Passwort gespeichert</h1>
<p>Dein Passwort wurde gespeichert. <a href="Account/Login">Jetzt anmelden</a></p>
```

`InvalidPasswordReset.razor`:

```razor
@page "/Account/InvalidPasswordReset"
<PageTitle>Ungültiger Link</PageTitle>

<h1>Ungültiger Link</h1>
<p>Der Link ist ungültig oder abgelaufen. Bitte den Admin um einen neuen Link.</p>
```

`Lockout.razor`:

```razor
@page "/Account/Lockout"
<PageTitle>Konto gesperrt</PageTitle>

<h1>Konto gesperrt</h1>
<p class="text-danger">Dieses Konto ist gesperrt. Bitte später erneut versuchen oder den Admin fragen.</p>
```

`AccessDenied.razor`:

```razor
@page "/Account/AccessDenied"
<PageTitle>Kein Zugriff</PageTitle>

<h1>Kein Zugriff</h1>
<p class="text-danger">Du hast keine Berechtigung für diese Seite.</p>
```

`InvalidUser.razor`:

```razor
@page "/Account/InvalidUser"
<PageTitle>Ungültiger Nutzer</PageTitle>

<h1>Ungültiger Nutzer</h1>
<StatusMessage />
```

`Manage/ChangePassword.razor` (neue Datei im Ordner `Components/Account/Pages/Manage/`):

```razor
@page "/Account/Manage/ChangePassword"
@attribute [Authorize]

@using System.ComponentModel.DataAnnotations
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Identity
@using Webionic.ICalMerger.Data

@inject UserManager<ApplicationUser> UserManager
@inject SignInManager<ApplicationUser> SignInManager
@inject IdentityRedirectManager RedirectManager

<PageTitle>Passwort ändern</PageTitle>

<h1>Passwort ändern</h1>
<div class="row">
    <div class="col-md-6">
        <StatusMessage Message="@message" />
        <EditForm Model="Input" FormName="change-password" OnValidSubmit="OnValidSubmitAsync" method="post">
            <DataAnnotationsValidator />
            <ValidationSummary class="text-danger" role="alert" />
            <div class="form-floating mb-3">
                <InputText type="password" @bind-Value="Input.OldPassword" id="Input.OldPassword" class="form-control" autocomplete="current-password" aria-required="true" placeholder="Aktuelles Passwort" />
                <label for="Input.OldPassword" class="form-label">Aktuelles Passwort</label>
                <ValidationMessage For="() => Input.OldPassword" class="text-danger" />
            </div>
            <div class="form-floating mb-3">
                <InputText type="password" @bind-Value="Input.NewPassword" id="Input.NewPassword" class="form-control" autocomplete="new-password" aria-required="true" placeholder="Neues Passwort" />
                <label for="Input.NewPassword" class="form-label">Neues Passwort</label>
                <ValidationMessage For="() => Input.NewPassword" class="text-danger" />
            </div>
            <div class="form-floating mb-3">
                <InputText type="password" @bind-Value="Input.ConfirmPassword" id="Input.ConfirmPassword" class="form-control" autocomplete="new-password" aria-required="true" placeholder="Neues Passwort wiederholen" />
                <label for="Input.ConfirmPassword" class="form-label">Neues Passwort wiederholen</label>
                <ValidationMessage For="() => Input.ConfirmPassword" class="text-danger" />
            </div>
            <button type="submit" class="w-100 btn btn-lg btn-primary">Passwort ändern</button>
        </EditForm>
    </div>
</div>

@code {
    private string? message;
    private ApplicationUser user = default!;

    [CascadingParameter]
    private HttpContext HttpContext { get; set; } = default!;

    [SupplyParameterFromForm]
    private InputModel Input { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        Input ??= new();

        var current = await UserManager.GetUserAsync(HttpContext.User);
        if (current is null)
        {
            RedirectManager.RedirectToInvalidUser(UserManager, HttpContext);
            return;
        }

        user = current;
    }

    private async Task OnValidSubmitAsync()
    {
        var result = await UserManager.ChangePasswordAsync(user, Input.OldPassword, Input.NewPassword);
        if (!result.Succeeded)
        {
            message = $"Fehler: {string.Join(" ", result.Errors.Select(e => e.Code == "PasswordMismatch" ? "Das aktuelle Passwort ist falsch." : e.Description))}";
            return;
        }

        await SignInManager.RefreshSignInAsync(user);
        RedirectManager.RedirectToCurrentPageWithStatus("Dein Passwort wurde geändert.", HttpContext);
    }

    private sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte das aktuelle Passwort eingeben.")]
        [DataType(DataType.Password)]
        public string OldPassword { get; set; } = "";

        [Required(ErrorMessage = "Bitte ein neues Passwort eingeben.")]
        [StringLength(100, ErrorMessage = "Das Passwort muss mindestens {2} Zeichen lang sein.", MinimumLength = 10)]
        [DataType(DataType.Password)]
        public string NewPassword { get; set; } = "";

        [DataType(DataType.Password)]
        [Compare("NewPassword", ErrorMessage = "Die Passwörter stimmen nicht überein.")]
        public string ConfirmPassword { get; set; } = "";
    }
}
```

- [ ] **Step 10: Layout und Startseite**

`Components/Layout/NavMenu.razor`:

```razor
@implements IDisposable

@inject NavigationManager NavigationManager

<div class="top-row ps-3 navbar navbar-dark">
    <div class="container-fluid">
        <a class="navbar-brand" href="">iCal Merger</a>
    </div>
</div>

<input type="checkbox" title="Navigationsmenü" class="navbar-toggler" />

<div class="nav-scrollable" onclick="document.querySelector('.navbar-toggler').click()">
    <nav class="nav flex-column">
        <AuthorizeView>
            <Authorized>
                <div class="nav-item px-3">
                    <NavLink class="nav-link" href="" Match="NavLinkMatch.All">
                        <span class="bi bi-house-door-fill-nav-menu" aria-hidden="true"></span> Meine Kalender
                    </NavLink>
                </div>
                <AuthorizeView Roles="Admin" Context="adminContext">
                    <div class="nav-item px-3">
                        <NavLink class="nav-link" href="admin/users">
                            <span class="bi bi-person-nav-menu" aria-hidden="true"></span> Nutzer
                        </NavLink>
                    </div>
                </AuthorizeView>
                <div class="nav-item px-3">
                    <NavLink class="nav-link" href="Account/Manage/ChangePassword">
                        <span class="bi bi-person-fill-nav-menu" aria-hidden="true"></span> @context.User.Identity?.Name
                    </NavLink>
                </div>
                <div class="nav-item px-3">
                    <form action="Account/Logout" method="post">
                        <AntiforgeryToken />
                        <input type="hidden" name="ReturnUrl" value="@currentUrl" />
                        <button type="submit" class="nav-link">
                            <span class="bi bi-arrow-bar-left-nav-menu" aria-hidden="true"></span> Abmelden
                        </button>
                    </form>
                </div>
            </Authorized>
            <NotAuthorized>
                <div class="nav-item px-3">
                    <NavLink class="nav-link" href="Account/Login">
                        <span class="bi bi-person-badge-nav-menu" aria-hidden="true"></span> Anmelden
                    </NavLink>
                </div>
            </NotAuthorized>
        </AuthorizeView>
    </nav>
</div>

@code {
    private string? currentUrl;

    protected override void OnInitialized()
    {
        currentUrl = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        currentUrl = NavigationManager.ToBaseRelativePath(e.Location);
        StateHasChanged();
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}
```

`Components/Layout/MainLayout.razor`:

```razor
@inherits LayoutComponentBase

<div class="page">
    <div class="sidebar">
        <NavMenu />
    </div>

    <main>
        <article class="content px-4 pt-4">
            @Body
        </article>
    </main>
</div>

<div id="blazor-error-ui" data-nosnippet>
    Es ist ein unerwarteter Fehler aufgetreten.
    <a href="." class="reload">Neu laden</a>
    <span class="dismiss">🗙</span>
</div>
```

`Components/Pages/Home.razor` (Platzhalter bis Task 8):

```razor
@page "/"
@attribute [Authorize]

<PageTitle>iCal Merger</PageTitle>

<h1>iCal Merger</h1>
```

Ergänze in `Components/_Imports.razor` die Zeile `@using Microsoft.AspNetCore.Authorization`.

- [ ] **Step 11: Build und Start prüfen**

```bash
dotnet build
```

Expected: Build succeeded, 0 Fehler. Behebe Compilerfehler, die von gelöschten Vorlagenteilen kommen (z. B. übrig gebliebene Verweise auf `ExternalLogin`, `PasskeyOperation`), indem du den verweisenden Code löschst, nicht indem du Vorlagenteile zurückholst.

```bash
dotnet run --project src/Webionic.ICalMerger --urls http://127.0.0.1:5099 &
sleep 6
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:5099/Account/Login
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:5099/Account/Register
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:5099/
kill %1
```

Expected: `200`, `404`, `302` (Weiterleitung zum Login). Danach `ls src/Webionic.ICalMerger/data/` zeigt `app.db`.

- [ ] **Step 12: Commit**

```bash
git add -A
git commit -m "feat: scaffold Blazor app with trimmed Identity and German account pages" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `IcsMerger` (reine Merge-Funktion)

**Files:**
- Create: `src/Webionic.ICalMerger/Merging/IcsMerger.cs`
- Create: `tests/Webionic.ICalMerger.Tests/Support/IcsFixtures.cs`
- Test: `tests/Webionic.ICalMerger.Tests/Merging/IcsMergerTests.cs`

**Interfaces:**
- Produces:
  - `static string IcsMerger.Merge(string calendarName, IReadOnlyList<string> sources)`. Die Quellen sind nach Priorität sortiert, die erste gewinnt bei Duplikaten.
  - `static bool IcsMerger.LooksLikeCalendar(string? ics)`
  - `IcsFixtures.Calendar(params string[] components)` und `IcsFixtures.Event(string uid, string summary = "Termin", string extra = "")` für alle späteren Tests.

- [ ] **Step 1: Fixtures anlegen**

`tests/Webionic.ICalMerger.Tests/Support/IcsFixtures.cs`:

```csharp
namespace Webionic.ICalMerger.Tests.Support;

internal static class IcsFixtures
{
    public const string Crlf = "\r\n";

    public static string Calendar(params string[] components) =>
        "BEGIN:VCALENDAR" + Crlf + "VERSION:2.0" + Crlf + "PRODID:-//Test//EN" + Crlf +
        string.Concat(components) + "END:VCALENDAR" + Crlf;

    public static string Event(string uid, string summary = "Termin", string extra = "") =>
        "BEGIN:VEVENT" + Crlf + $"UID:{uid}" + Crlf + "DTSTAMP:20260101T000000Z" + Crlf +
        "DTSTART:20260105T100000Z" + Crlf + $"SUMMARY:{summary}" + Crlf + extra + "END:VEVENT" + Crlf;
}
```

- [ ] **Step 2: Failing Tests schreiben**

`tests/Webionic.ICalMerger.Tests/Merging/IcsMergerTests.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using Webionic.ICalMerger.Merging;
using static Webionic.ICalMerger.Tests.Support.IcsFixtures;

namespace Webionic.ICalMerger.Tests.Merging;

public class IcsMergerTests
{
    private const string Vienna =
        "BEGIN:VTIMEZONE\r\nTZID:Europe/Vienna\r\nBEGIN:STANDARD\r\nDTSTART:19701025T030000\r\n" +
        "TZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n";

    private static int Count(string text, string needle) => text.Split(needle).Length - 1;

    private static string Unfold(string ics) => ics.Replace("\r\n ", "");

    [Fact]
    public void Merge_CombinesEventsFromAllSources()
    {
        var result = IcsMerger.Merge("Familie", [Calendar(Event("a@x")), Calendar(Event("b@x"))]);

        Assert.Equal(1, Count(result, "BEGIN:VCALENDAR"));
        Assert.Equal(1, Count(result, "END:VCALENDAR"));
        Assert.Contains("UID:a@x", result);
        Assert.Contains("UID:b@x", result);
        Assert.Contains("PRODID:-//Webionic//iCalMerger//EN", result);
        Assert.Contains("X-WR-CALNAME:Familie", result);
    }

    [Fact]
    public void Merge_DeduplicatesByUid_FirstSourceWins()
    {
        var result = IcsMerger.Merge("X", [Calendar(Event("dup@x", "Erste")), Calendar(Event("dup@x", "Zweite"))]);

        Assert.Equal(1, Count(result, "BEGIN:VEVENT"));
        Assert.Contains("SUMMARY:Erste", result);
        Assert.DoesNotContain("SUMMARY:Zweite", result);
    }

    [Fact]
    public void Merge_KeepsRecurrenceOverridesAndDeduplicatesThem()
    {
        var master = Event("r@x", "Serie", "RRULE:FREQ=WEEKLY\r\n");
        var exception = Event("r@x", "Ausnahme", "RECURRENCE-ID:20260112T100000Z\r\n");

        var result = IcsMerger.Merge("X", [Calendar(master, exception), Calendar(exception)]);

        Assert.Equal(2, Count(result, "BEGIN:VEVENT"));
        Assert.Contains("SUMMARY:Serie", result);
        Assert.Contains("SUMMARY:Ausnahme", result);
    }

    [Fact]
    public void Merge_KeepsEventsWithoutUid()
    {
        const string noUid = "BEGIN:VEVENT\r\nDTSTART:20260105T100000Z\r\nSUMMARY:Ohne\r\nEND:VEVENT\r\n";

        var result = IcsMerger.Merge("X", [Calendar(noUid), Calendar(noUid)]);

        Assert.Equal(2, Count(result, "BEGIN:VEVENT"));
    }

    [Fact]
    public void Merge_EmitsEachTimezoneOncePerTzid()
    {
        var newYork = Vienna.Replace("Europe/Vienna", "America/New_York");

        var result = IcsMerger.Merge("X", [Calendar(Vienna, Event("a@x")), Calendar(Vienna, newYork, Event("b@x"))]);

        Assert.Equal(2, Count(result, "BEGIN:VTIMEZONE"));
        Assert.Equal(1, Count(result, "TZID:Europe/Vienna"));
        Assert.Equal(1, Count(result, "TZID:America/New_York"));
    }

    [Fact]
    public void Merge_DropsComponentsOtherThanEventAndTimezone()
    {
        var todo = "BEGIN:VTODO\r\nUID:t@x\r\nSUMMARY:Aufgabe\r\nEND:VTODO\r\n";
        var journal = "BEGIN:VJOURNAL\r\nUID:j@x\r\nEND:VJOURNAL\r\n";

        var result = IcsMerger.Merge("X", [Calendar(todo, journal, Event("a@x"))]);

        Assert.DoesNotContain("VTODO", result);
        Assert.DoesNotContain("VJOURNAL", result);
        Assert.Contains("UID:a@x", result);
    }

    [Fact]
    public void Merge_KeepsNestedAlarms()
    {
        var alarm = "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT15M\r\nEND:VALARM\r\n";

        var result = IcsMerger.Merge("X", [Calendar(Event("alarm@x", extra: alarm))]);

        Assert.Contains("BEGIN:VALARM", result);
        Assert.Contains("TRIGGER:-PT15M", result);
    }

    [Fact]
    public void Merge_UnfoldsInputAndRefoldsOutputAt75Octets()
    {
        var summary = new string('x', 200);
        var folded = "BEGIN:VEVENT\r\nUID:long@x\r\nSUMMARY:" + summary[..50] + "\r\n " + summary[50..120] +
                     "\r\n\t" + summary[120..] + "\r\nEND:VEVENT\r\n";

        var result = IcsMerger.Merge("X", [Calendar(folded)]);

        Assert.Contains("SUMMARY:" + summary, Unfold(result));
        foreach (var line in result.Split("\r\n"))
        {
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, $"Zeile zu lang: {line}");
        }
    }

    [Theory]
    [InlineData("ä")]
    [InlineData("😀")]
    public void Merge_FoldsMultibyteTextWithoutSplittingCharacters(string character)
    {
        var summary = string.Concat(Enumerable.Repeat(character, 100));

        var result = IcsMerger.Merge("X", [Calendar(Event("mb@x", summary))]);

        Assert.Contains("SUMMARY:" + summary, Unfold(result));
        foreach (var line in result.Split("\r\n"))
        {
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75);
        }
    }

    [Fact]
    public void Merge_HandlesBomAndLineFeedOnlyInput()
    {
        var source = "\uFEFF" + Calendar(Event("bom@x")).Replace("\r\n", "\n");

        var result = IcsMerger.Merge("X", [source]);

        Assert.Contains("UID:bom@x", result);
    }

    [Fact]
    public void Merge_HandlesHugeFoldedAttachmentQuickly()
    {
        var base64 = Convert.ToBase64String(new byte[3_000_000]);
        var folded = new StringBuilder("ATTACH;VALUE=BINARY:");
        for (var i = 0; i < base64.Length; i += 74)
        {
            folded.Append("\r\n ").Append(base64, i, Math.Min(74, base64.Length - i));
        }
        var source = Calendar("BEGIN:VEVENT\r\nUID:big@x\r\n" + folded + "\r\nEND:VEVENT\r\n");

        var stopwatch = Stopwatch.StartNew();
        var result = IcsMerger.Merge("X", [source]);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Dauerte {stopwatch.Elapsed}");
        Assert.Contains("UID:big@x", result);
    }

    [Fact]
    public void Merge_DropsTruncatedEventButKeepsTheRest()
    {
        var truncated = "BEGIN:VEVENT\r\nUID:cut@x\r\nSUMMARY:abgeschnitten\r\n";

        var result = IcsMerger.Merge("X", [Calendar(truncated, Event("ok@x")), Calendar(truncated)]);

        Assert.DoesNotContain("cut@x", result);
        Assert.Contains("UID:ok@x", result);
    }

    [Fact]
    public void Merge_EscapesCalendarName()
    {
        var result = IcsMerger.Merge("Familie, Papa; \\ \"Mama\"\nX", []);

        Assert.Contains("X-WR-CALNAME:Familie\\, Papa\\; \\\\ \"Mama\"\\nX", result);
    }

    [Fact]
    public void Merge_WithoutSources_ReturnsValidEmptyCalendar()
    {
        var result = IcsMerger.Merge("Leer", []);

        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n", result);
        Assert.EndsWith("END:VCALENDAR\r\n", result);
        Assert.Equal(0, Count(result, "BEGIN:VEVENT"));
    }

    [Fact]
    public void Merge_UsesCrlfOnly()
    {
        var result = IcsMerger.Merge("X", [Calendar(Event("a@x"))]);

        Assert.DoesNotContain("\n", result.Replace("\r\n", ""));
    }

    [Theory]
    [InlineData("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", true)]
    [InlineData("\uFEFFBEGIN:VCALENDAR\nEND:VCALENDAR", true)]
    [InlineData("begin:vcalendar\r\nend:vcalendar", true)]
    [InlineData("<html><body>Bitte anmelden</body></html>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeCalendar_DetectsCalendars(string? input, bool expected)
    {
        Assert.Equal(expected, IcsMerger.LooksLikeCalendar(input));
    }
}
```

- [ ] **Step 3: Test laufen lassen, Fehlschlag prüfen**

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~IcsMergerTests"`
Expected: Build-Fehler, `IcsMerger` existiert nicht.

- [ ] **Step 4: Implementierung**

`src/Webionic.ICalMerger/Merging/IcsMerger.cs`:

```csharp
using System.Text;

namespace Webionic.ICalMerger.Merging;

/// <summary>
/// Fasst mehrere iCalendar-Texte textbasiert zusammen. Die Blöcke VEVENT und VTIMEZONE werden
/// unverändert übernommen, alles andere entfällt.
/// </summary>
public static class IcsMerger
{
    private const int MaxLineOctets = 75;

    public static bool LooksLikeCalendar(string? ics) =>
        !string.IsNullOrEmpty(ics) &&
        Unfold(ics).Any(line => line.TrimEnd().Equals("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase));

    /// <param name="sources">iCalendar-Texte, nach Priorität sortiert. Bei Duplikaten gewinnt der erste.</param>
    public static string Merge(string calendarName, IReadOnlyList<string> sources)
    {
        var timezones = new List<List<string>>();
        var seenTimezones = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<List<string>>();
        var seenEvents = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            foreach (var block in ExtractBlocks(Unfold(source)))
            {
                if (block[0].Equals("BEGIN:VTIMEZONE", StringComparison.OrdinalIgnoreCase))
                {
                    var tzid = TopLevelValue(block, "TZID");
                    if (tzid is null || seenTimezones.Add(tzid))
                    {
                        timezones.Add(block);
                    }
                    continue;
                }

                var uid = TopLevelValue(block, "UID");
                if (uid is null)
                {
                    events.Add(block);
                    continue;
                }

                var recurrenceId = TopLevelLine(block, "RECURRENCE-ID") ?? "";
                if (seenEvents.Add(uid + "\n" + recurrenceId))
                {
                    events.Add(block);
                }
            }
        }

        var output = new StringBuilder();
        AppendFolded(output, "BEGIN:VCALENDAR");
        AppendFolded(output, "VERSION:2.0");
        AppendFolded(output, "PRODID:-//Webionic//iCalMerger//EN");
        AppendFolded(output, "CALSCALE:GREGORIAN");
        AppendFolded(output, "X-WR-CALNAME:" + EscapeText(calendarName));
        AppendFolded(output, "REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        AppendFolded(output, "X-PUBLISHED-TTL:PT1H");
        foreach (var line in timezones.SelectMany(b => b)) AppendFolded(output, line);
        foreach (var line in events.SelectMany(b => b)) AppendFolded(output, line);
        AppendFolded(output, "END:VCALENDAR");
        return output.ToString();
    }

    /// <summary>Entfernt BOM, vereinheitlicht Zeilenenden und fügt gefaltete Zeilen wieder zusammen (RFC 5545 §3.1).</summary>
    private static List<string> Unfold(string text)
    {
        var lines = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
        }

        var normalized = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var raw in normalized.Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && current.Length > 0)
            {
                current.Append(raw, 1, raw.Length - 1);
                continue;
            }

            Flush();
            current.Append(raw);
        }

        Flush();
        return lines;
    }

    /// <summary>Liefert vollständige VEVENT- und VTIMEZONE-Blöcke. Unvollständige Blöcke werden verworfen.</summary>
    private static IEnumerable<List<string>> ExtractBlocks(List<string> lines)
    {
        List<string>? current = null;
        string beginMarker = "";
        string endMarker = "";

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (current is null)
            {
                if (IsBegin(line, "VEVENT") || IsBegin(line, "VTIMEZONE"))
                {
                    beginMarker = line.ToUpperInvariant();
                    endMarker = "END:" + beginMarker["BEGIN:".Length..];
                    current = [line];
                }
                continue;
            }

            if (line.Equals(beginMarker, StringComparison.OrdinalIgnoreCase))
            {
                // Der vorige Block war nicht abgeschlossen: verwerfen und neu beginnen.
                current = [line];
                continue;
            }

            current.Add(line);
            if (line.Equals(endMarker, StringComparison.OrdinalIgnoreCase))
            {
                yield return current;
                current = null;
            }
        }
    }

    private static bool IsBegin(string line, string component) =>
        line.Equals("BEGIN:" + component, StringComparison.OrdinalIgnoreCase);

    /// <summary>Sucht eine Eigenschaft auf oberster Ebene des Blocks (nicht in verschachtelten Komponenten).</summary>
    private static string? TopLevelLine(List<string> block, string name)
    {
        var depth = 0;
        for (var i = 1; i < block.Count - 1; i++)
        {
            var line = block[i];
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) { depth++; continue; }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase)) { depth--; continue; }
            if (depth != 0) continue;

            var end = line.IndexOfAny([':', ';']);
            if (end > 0 && line.AsSpan(0, end).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }
        return null;
    }

    private static string? TopLevelValue(List<string> block, string name)
    {
        var line = TopLevelLine(block, name);
        if (line is null) return null;
        var colon = line.IndexOf(':');
        return colon < 0 ? null : line[(colon + 1)..].Trim();
    }

    private static string EscapeText(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
            .Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    /// <summary>Schreibt eine Zeile mit CRLF und faltet sie bei 75 Oktetten, ohne Zeichen zu zerteilen.</summary>
    private static void AppendFolded(StringBuilder output, string line)
    {
        var octets = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (octets + size > MaxLineOctets)
            {
                output.Append("\r\n ");
                octets = 1;
            }

            if (rune.IsBmp) output.Append((char)rune.Value);
            else output.Append(rune.ToString());
            octets += size;
        }
        output.Append("\r\n");
    }
}
```

- [ ] **Step 5: Tests laufen lassen**

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~IcsMergerTests"`
Expected: alle PASS. Schlägt der Test mit dem 3-MB-`ATTACH` wegen der Zeit fehl, ist irgendwo eine quadratische Stringverkettung im Code, nicht das Limit lockern.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add text-based IcsMerger" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `SafeHttpFetcher` mit SSRF-Schutz

**Files:**
- Create: `src/Webionic.ICalMerger/Fetching/IpGuard.cs`, `src/Webionic.ICalMerger/Fetching/SafeHttpFetcher.cs`, `src/Webionic.ICalMerger/ServiceRegistration.cs`
- Modify: `src/Webionic.ICalMerger/Program.cs` (Aufruf der Registrierung)
- Create: `tests/Webionic.ICalMerger.Tests/Support/LocalHttpServer.cs`
- Test: `tests/Webionic.ICalMerger.Tests/Fetching/IpGuardTests.cs`, `tests/Webionic.ICalMerger.Tests/Fetching/SafeHttpFetcherTests.cs`

**Interfaces:**
- Produces:
  - `interface ICalendarFetcher { Task<string> FetchAsync(string url, CancellationToken ct); }`
  - `sealed class FetchException(string message, Exception? inner = null) : Exception` (deutsche Meldung, enthält nie die URL)
  - `sealed record FetcherOptions(TimeSpan Timeout, long MaxBytes)` mit `FetcherOptions.Default`
  - `sealed class SafeHttpFetcher(HttpClient client, FetcherOptions? options = null) : ICalendarFetcher`
  - `static Uri SafeHttpFetcher.NormalizeUrl(string url)` (wirft `FetchException("Ungültige URL")`)
  - `static SocketsHttpHandler SafeHttpFetcher.CreateHandler(Func<IPAddress, bool>? isAllowed = null)`
  - `static bool IpGuard.IsPublic(IPAddress address)`
  - `static IServiceCollection ServiceRegistration.AddCalendarServices(this IServiceCollection services, IConfiguration configuration)`

- [ ] **Step 1: Failing Tests für `IpGuard`**

`tests/Webionic.ICalMerger.Tests/Fetching/IpGuardTests.cs`:

```csharp
using System.Net;
using Webionic.ICalMerger.Fetching;

namespace Webionic.ICalMerger.Tests.Fetching;

public class IpGuardTests
{
    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.8.8")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    public void IsPublic_BlocksInternalAddresses(string address)
    {
        Assert.False(IpGuard.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:8.8.8.8")]
    public void IsPublic_AllowsPublicAddresses(string address)
    {
        Assert.True(IpGuard.IsPublic(IPAddress.Parse(address)));
    }
}
```

- [ ] **Step 2: `IpGuard` implementieren**

`src/Webionic.ICalMerger/Fetching/IpGuard.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace Webionic.ICalMerger.Fetching;

public static class IpGuard
{
    /// <summary>True, wenn die Adresse ein öffentliches Ziel ist (kein Loopback, privat, Link-Local usw.).</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal);
        }

        var b = address.GetAddressBytes();
        return !(b[0] == 0
                 || b[0] == 10
                 || (b[0] == 100 && b[1] is >= 64 and <= 127)
                 || (b[0] == 169 && b[1] == 254)
                 || (b[0] == 172 && b[1] is >= 16 and <= 31)
                 || (b[0] == 192 && b[1] == 168)
                 || b[0] >= 224);
    }
}
```

- [ ] **Step 3: Test-Server-Helfer**

`tests/Webionic.ICalMerger.Tests/Support/LocalHttpServer.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace Webionic.ICalMerger.Tests.Support;

/// <summary>Minimaler Kestrel-Server auf Loopback mit zufälligem Port.</summary>
internal sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LocalHttpServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string BaseUrl { get; }

    public static async Task<LocalHttpServer> StartAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return new LocalHttpServer(app, app.Urls.First());
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
```

- [ ] **Step 4: Failing Tests für den Fetcher**

`tests/Webionic.ICalMerger.Tests/Fetching/SafeHttpFetcherTests.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Webionic.ICalMerger.Fetching;
using Webionic.ICalMerger.Tests.Support;

namespace Webionic.ICalMerger.Tests.Fetching;

public class SafeHttpFetcherTests
{
    private static SafeHttpFetcher Create(FetcherOptions? options = null, bool allowLoopback = true) =>
        new(new HttpClient(SafeHttpFetcher.CreateHandler(allowLoopback ? _ => true : null)), options);

    [Theory]
    [InlineData("webcal://example.com/a.ics", "https://example.com/a.ics")]
    [InlineData("WEBCAL://example.com/a.ics", "https://example.com/a.ics")]
    [InlineData("webcals://example.com/a.ics", "https://example.com/a.ics")]
    [InlineData("  https://example.com/a.ics  ", "https://example.com/a.ics")]
    [InlineData("http://example.com/a.ics", "http://example.com/a.ics")]
    public void NormalizeUrl_RewritesAndTrims(string input, string expected)
    {
        Assert.Equal(expected, SafeHttpFetcher.NormalizeUrl(input).ToString());
    }

    [Theory]
    [InlineData("ftp://example.com/a.ics")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("kein url")]
    [InlineData("")]
    public void NormalizeUrl_RejectsInvalidUrls(string input)
    {
        var ex = Assert.Throws<FetchException>(() => SafeHttpFetcher.NormalizeUrl(input));
        Assert.Equal("Ungültige URL", ex.Message);
    }

    [Fact]
    public async Task Fetch_ReturnsBody()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/cal.ics", () => Results.Text("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", "text/calendar")));

        var body = await Create().FetchAsync(server.BaseUrl + "/cal.ics", CancellationToken.None);

        Assert.StartsWith("BEGIN:VCALENDAR", body);
    }

    [Fact]
    public async Task Fetch_BlocksLoopbackByDefault()
    {
        await using var server = await LocalHttpServer.StartAsync(app => app.MapGet("/cal.ics", () => "x"));

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            Create(allowLoopback: false).FetchAsync(server.BaseUrl + "/cal.ics", CancellationToken.None));

        Assert.Contains("nicht erlaubt", ex.Message);
    }

    [Fact]
    public async Task Fetch_NonSuccessStatus_Throws()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/missing.ics", () => Results.NotFound()));

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            Create().FetchAsync(server.BaseUrl + "/missing.ics", CancellationToken.None));

        Assert.Equal("HTTP 404", ex.Message);
    }

    [Fact]
    public async Task Fetch_RejectsTooLargeResponseWithContentLength()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/big.ics", () => Results.Text(new string('x', 5000), "text/calendar")));

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            Create(new FetcherOptions(TimeSpan.FromSeconds(5), 1000)).FetchAsync(server.BaseUrl + "/big.ics", CancellationToken.None));

        Assert.Contains("zu groß", ex.Message);
    }

    [Fact]
    public async Task Fetch_RejectsTooLargeChunkedResponse()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/chunked.ics", async (HttpContext ctx) =>
            {
                for (var i = 0; i < 10; i++)
                {
                    await ctx.Response.WriteAsync(new string('x', 500));
                    await ctx.Response.Body.FlushAsync();
                }
            }));

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            Create(new FetcherOptions(TimeSpan.FromSeconds(5), 2000)).FetchAsync(server.BaseUrl + "/chunked.ics", CancellationToken.None));

        Assert.Contains("zu groß", ex.Message);
    }

    [Fact]
    public async Task Fetch_TimesOut()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/slow.ics", async (HttpContext ctx) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted);
                return "x";
            }));

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            Create(new FetcherOptions(TimeSpan.FromMilliseconds(300), 1000)).FetchAsync(server.BaseUrl + "/slow.ics", CancellationToken.None));

        Assert.Contains("Zeitüberschreitung", ex.Message);
    }

    [Fact]
    public async Task Fetch_FollowsRedirects()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
        {
            app.MapGet("/a", () => Results.Redirect("/b"));
            app.MapGet("/b", () => Results.Text("BEGIN:VCALENDAR\r\nEND:VCALENDAR", "text/calendar"));
        });

        var body = await Create().FetchAsync(server.BaseUrl + "/a", CancellationToken.None);

        Assert.StartsWith("BEGIN:VCALENDAR", body);
    }

    [Fact]
    public async Task Fetch_StopsOnRedirectLoop()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/loop", () => Results.Redirect("/loop")));

        await Assert.ThrowsAsync<FetchException>(() =>
            Create().FetchAsync(server.BaseUrl + "/loop", CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_CallerCancellation_IsNotReportedAsTimeout()
    {
        await using var server = await LocalHttpServer.StartAsync(app =>
            app.MapGet("/slow.ics", async (HttpContext ctx) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted);
                return "x";
            }));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create().FetchAsync(server.BaseUrl + "/slow.ics", cts.Token));
    }
}
```

- [ ] **Step 5: Tests laufen lassen, Fehlschlag prüfen**

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~Fetching"`
Expected: Build-Fehler, `SafeHttpFetcher`, `FetchException` usw. fehlen.

- [ ] **Step 6: `SafeHttpFetcher` implementieren**

`src/Webionic.ICalMerger/Fetching/SafeHttpFetcher.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Webionic.ICalMerger.Fetching;

public interface ICalendarFetcher
{
    /// <exception cref="FetchException">Abruf nicht möglich. Die Meldung ist deutsch und enthält nie die URL.</exception>
    Task<string> FetchAsync(string url, CancellationToken ct);
}

public sealed class FetchException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record FetcherOptions(TimeSpan Timeout, long MaxBytes)
{
    public static FetcherOptions Default { get; } = new(TimeSpan.FromSeconds(10), 10 * 1024 * 1024);
}

public sealed class SafeHttpFetcher(HttpClient client, FetcherOptions? options = null) : ICalendarFetcher
{
    private readonly FetcherOptions _options = options ?? FetcherOptions.Default;

    public static Uri NormalizeUrl(string url)
    {
        var trimmed = (url ?? "").Trim();
        foreach (var scheme in new[] { "webcals://", "webcal://" })
        {
            if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "https://" + trimmed[scheme.Length..];
                break;
            }
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new FetchException("Ungültige URL");
        }

        return uri;
    }

    /// <summary>
    /// Handler, der die Ziel-IP beim Verbindungsaufbau prüft. Das deckt auch Redirects und DNS-Rebinding ab,
    /// weil jede Verbindung neu geprüft wird.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(Func<IPAddress, bool>? isAllowed = null)
    {
        isAllowed ??= IpGuard.IsPublic;

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var allowed = addresses.Where(isAllowed).ToArray();
                if (allowed.Length == 0)
                {
                    throw new BlockedAddressException();
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    public async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        var uri = NormalizeUrl(url);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new FetchException($"HTTP {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength > _options.MaxBytes)
            {
                throw new FetchException("Antwort ist zu groß");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await ReadLimitedAsync(stream, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FetchException("Zeitüberschreitung beim Abruf");
        }
        catch (HttpRequestException ex)
        {
            throw new FetchException(
                ex.GetBaseException() is BlockedAddressException
                    ? "Adresse nicht erlaubt (interne Netzwerke sind gesperrt)"
                    : "Quelle nicht erreichbar",
                ex);
        }
    }

    private async Task<string> ReadLimitedAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > _options.MaxBytes)
            {
                throw new FetchException("Antwort ist zu groß");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private sealed class BlockedAddressException : Exception;
}
```

- [ ] **Step 7: Registrierung**

`src/Webionic.ICalMerger/ServiceRegistration.cs`:

```csharp
using Webionic.ICalMerger.Fetching;

namespace Webionic.ICalMerger;

public static class ServiceRegistration
{
    public static IServiceCollection AddCalendarServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ICalendarFetcher>(_ =>
        {
            var client = new HttpClient(SafeHttpFetcher.CreateHandler(), disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan, // Das Zeitlimit steuert der Fetcher selbst.
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("iCalMerger/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/calendar");
            return new SafeHttpFetcher(client);
        });

        return services;
    }
}
```

In `Program.cs` direkt nach `builder.Services.Configure<ForwardedHeadersOptions>(...)` ergänzen:

```csharp
builder.Services.AddCalendarServices(builder.Configuration);
```

Hinweis: `using Webionic.ICalMerger;` oben in `Program.cs` ergänzen, falls nötig (der Namespace ist meist durch ImplicitUsings der Vorlage nicht enthalten).

- [ ] **Step 8: Tests laufen lassen**

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~Fetching"`
Expected: alle PASS. Schlägt `Fetch_BlocksLoopbackByDefault` fehl, weil die Meldung nicht „nicht erlaubt“ enthält, ist die `BlockedAddressException` nicht als Basis-Exception angekommen. Prüfe `GetBaseException()` an der echten Exception-Kette, ändere nicht den Test.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: add SSRF-safe calendar fetcher" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Datenmodell, Migration und `CalendarService`

**Files:**
- Create: `src/Webionic.ICalMerger/Data/MergedCalendar.cs`, `Data/CalendarSource.cs`, `Data/DesignTimeDbContextFactory.cs`, `Data/Migrations/*` (generiert)
- Modify: `src/Webionic.ICalMerger/Data/ApplicationDbContext.cs`
- Create: `src/Webionic.ICalMerger/Calendars/CalendarService.cs`, `Calendars/AppLimits.cs`, `Calendars/DomainException.cs`, `Calendars/TokenGenerator.cs`
- Modify: `src/Webionic.ICalMerger/ServiceRegistration.cs`
- Create: `tests/Webionic.ICalMerger.Tests/Support/TestDb.cs`, `Support/FakeFetcher.cs`
- Test: `tests/Webionic.ICalMerger.Tests/Data/MigrationsTests.cs`, `tests/Webionic.ICalMerger.Tests/Calendars/CalendarServiceTests.cs`

**Interfaces:**
- Consumes: `ICalendarFetcher`, `FetchException`, `SafeHttpFetcher.NormalizeUrl`, `IcsMerger.LooksLikeCalendar`, `ApplicationDbContext`.
- Produces:
  - Entities `MergedCalendar { int Id; string OwnerId; ApplicationUser? Owner; string Name; string Token; DateTime CreatedAt; List<CalendarSource> Sources }` und `CalendarSource { int Id; int MergedCalendarId; MergedCalendar? MergedCalendar; string Name; string Url; int SortOrder; DateTime? LastAttemptAt; DateTime? LastSuccessAt; string? LastError }` (alle `DateTime` in UTC).
  - `ApplicationDbContext.Calendars`, `ApplicationDbContext.Sources`
  - `class DomainException(string message) : Exception`, `sealed class AppLimits { int MaxCalendarsPerUser = 10; int MaxSourcesPerCalendar = 20 }`, `static string TokenGenerator.NewToken()` (43 Zeichen Base64Url)
  - `CalendarService(IDbContextFactory<ApplicationDbContext>, ICalendarFetcher, IOptions<AppLimits>)` mit: `ListAsync(string ownerId)`, `GetAsync(string ownerId, int id)`, `CreateAsync(string ownerId, string name)`, `RenameAsync(string ownerId, int id, string name)`, `DeleteAsync(string ownerId, int id)`, `RegenerateTokenAsync(string ownerId, int id)` (gibt das neue Token zurück), `AddSourceAsync(string ownerId, int calendarId, string name, string url, CancellationToken ct = default)`, `UpdateSourceAsync(string ownerId, int sourceId, string name, string url, CancellationToken ct = default)`, `DeleteSourceAsync(string ownerId, int sourceId)`, `MoveSourceAsync(string ownerId, int sourceId, int direction)`. Fremde oder unbekannte IDs führen zu `DomainException("Kalender nicht gefunden.")` bzw. `DomainException("Quelle nicht gefunden.")`. Außer `GetAsync`, das `null` liefert.

- [ ] **Step 1: Entities und DbContext**

`Data/MergedCalendar.cs`:

```csharp
namespace Webionic.ICalMerger.Data;

public class MergedCalendar
{
    public int Id { get; set; }
    public string OwnerId { get; set; } = "";
    public ApplicationUser? Owner { get; set; }
    public string Name { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<CalendarSource> Sources { get; set; } = [];
}
```

`Data/CalendarSource.cs`:

```csharp
namespace Webionic.ICalMerger.Data;

public class CalendarSource
{
    public int Id { get; set; }
    public int MergedCalendarId { get; set; }
    public MergedCalendar? MergedCalendar { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public int SortOrder { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
}
```

`Data/ApplicationDbContext.cs`:

```csharp
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Webionic.ICalMerger.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<MergedCalendar> Calendars => Set<MergedCalendar>();
    public DbSet<CalendarSource> Sources => Set<CalendarSource>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<MergedCalendar>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(100).IsRequired();
            e.Property(c => c.Token).HasMaxLength(64).IsRequired();
            e.HasIndex(c => c.Token).IsUnique();
            e.HasOne(c => c.Owner).WithMany().HasForeignKey(c => c.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Sources).WithOne(s => s.MergedCalendar).HasForeignKey(s => s.MergedCalendarId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CalendarSource>(e =>
        {
            e.Property(s => s.Name).HasMaxLength(100).IsRequired();
            e.Property(s => s.Url).HasMaxLength(2000).IsRequired();
            e.Property(s => s.LastError).HasMaxLength(500);
        });
    }
}
```

`Data/DesignTimeDbContextFactory.cs` (damit `dotnet ef` nicht `Program` startet):

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Webionic.ICalMerger.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=data/design.db").Options);
}
```

- [ ] **Step 2: Migration erzeugen**

```bash
dotnet new tool-manifest
dotnet tool install dotnet-ef
dotnet ef migrations add AddCalendars --project src/Webionic.ICalMerger --output-dir Data/Migrations
```

Expected: neue Dateien `Data/Migrations/*_AddCalendars.cs`. Prüfe, dass `Up` nur die Tabellen `Calendars` und `Sources` (mit Index auf `Token` und Cascade-FKs) anlegt und nichts an den Identity-Tabellen ändert.

- [ ] **Step 3: Test-Helfer**

`tests/Webionic.ICalMerger.Tests/Support/TestDb.cs`:

```csharp
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
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

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
```

`tests/Webionic.ICalMerger.Tests/Support/FakeFetcher.cs`:

```csharp
using System.Collections.Concurrent;
using Webionic.ICalMerger.Fetching;

namespace Webionic.ICalMerger.Tests.Support;

internal sealed class FakeFetcher : ICalendarFetcher
{
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<string>>> _responses = new();
    private readonly ConcurrentDictionary<string, int> _calls = new();

    public void Set(string url, string ics) => _responses[url] = _ => Task.FromResult(ics);

    public void SetAsync(string url, Func<CancellationToken, Task<string>> response) => _responses[url] = response;

    public void Fail(string url, string message = "Quelle nicht erreichbar") =>
        _responses[url] = _ => throw new FetchException(message);

    public int CallCount(string url) => _calls.GetValueOrDefault(url);

    public Task<string> FetchAsync(string url, CancellationToken ct)
    {
        _calls.AddOrUpdate(url, 1, (_, n) => n + 1);
        return _responses.TryGetValue(url, out var response)
            ? response(ct)
            : throw new FetchException("HTTP 404");
    }
}
```

- [ ] **Step 4: Failing Tests**

`tests/Webionic.ICalMerger.Tests/Data/MigrationsTests.cs`:

```csharp
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
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);

        await db.Database.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges(), "Modell und Migrationen weichen ab: neue Migration erzeugen.");
        Assert.Equal(0, await db.Calendars.CountAsync());
        Assert.Equal(0, await db.Sources.CountAsync());
    }
}
```

`tests/Webionic.ICalMerger.Tests/Calendars/CalendarServiceTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Webionic.ICalMerger.Calendars;
using Webionic.ICalMerger.Tests.Support;
using static Webionic.ICalMerger.Tests.Support.IcsFixtures;

namespace Webionic.ICalMerger.Tests.Calendars;

public sealed class CalendarServiceTests : IDisposable
{
    private const string UrlA = "https://example.com/a.ics";
    private const string UrlB = "https://example.com/b.ics";

    private readonly TestDb _db = new();
    private readonly FakeFetcher _fetcher = new();
    private readonly CalendarService _service;
    private readonly string _alice;
    private readonly string _bob;

    public CalendarServiceTests()
    {
        _alice = _db.AddUserAsync("alice@example.com").GetAwaiter().GetResult();
        _bob = _db.AddUserAsync("bob@example.com").GetAwaiter().GetResult();
        _fetcher.Set(UrlA, Calendar(Event("a@x")));
        _fetcher.Set(UrlB, Calendar(Event("b@x")));
        _service = new CalendarService(_db, _fetcher,
            Options.Create(new AppLimits { MaxCalendarsPerUser = 2, MaxSourcesPerCalendar = 2 }));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_StoresCalendarWithRandomToken()
    {
        var first = await _service.CreateAsync(_alice, "  Familie  ");
        var second = await _service.CreateAsync(_alice, "Arbeit");

        Assert.Equal("Familie", first.Name);
        Assert.Equal(43, first.Token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", first.Token);
        Assert.NotEqual(first.Token, second.Token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_RejectsEmptyName(string name)
    {
        await Assert.ThrowsAsync<DomainException>(() => _service.CreateAsync(_alice, name));
    }

    [Fact]
    public async Task Create_RejectsTooLongName()
    {
        await Assert.ThrowsAsync<DomainException>(() => _service.CreateAsync(_alice, new string('x', 101)));
    }

    [Fact]
    public async Task Create_EnforcesCalendarLimitPerUser()
    {
        await _service.CreateAsync(_alice, "1");
        await _service.CreateAsync(_alice, "2");

        var ex = await Assert.ThrowsAsync<DomainException>(() => _service.CreateAsync(_alice, "3"));
        Assert.Contains("2", ex.Message);

        await _service.CreateAsync(_bob, "Bobs erster");
    }

    [Fact]
    public async Task List_ReturnsOnlyOwnCalendarsWithSources()
    {
        var mine = await _service.CreateAsync(_alice, "Meiner");
        await _service.CreateAsync(_bob, "Fremder");
        await _service.AddSourceAsync(_alice, mine.Id, "A", UrlA);

        var list = await _service.ListAsync(_alice);

        var only = Assert.Single(list);
        Assert.Equal("Meiner", only.Name);
        Assert.Single(only.Sources);
    }

    [Fact]
    public async Task Get_OfForeignCalendar_ReturnsNull()
    {
        var bobs = await _service.CreateAsync(_bob, "Bobs");

        Assert.Null(await _service.GetAsync(_alice, bobs.Id));
        Assert.NotNull(await _service.GetAsync(_bob, bobs.Id));
    }

    [Fact]
    public async Task MutationsOnForeignCalendar_AreRejected()
    {
        var bobs = await _service.CreateAsync(_bob, "Bobs");

        await Assert.ThrowsAsync<DomainException>(() => _service.RenameAsync(_alice, bobs.Id, "Meins"));
        await Assert.ThrowsAsync<DomainException>(() => _service.DeleteAsync(_alice, bobs.Id));
        await Assert.ThrowsAsync<DomainException>(() => _service.RegenerateTokenAsync(_alice, bobs.Id));
        await Assert.ThrowsAsync<DomainException>(() => _service.AddSourceAsync(_alice, bobs.Id, "A", UrlA));
        Assert.Equal("Bobs", (await _service.GetAsync(_bob, bobs.Id))!.Name);
    }

    [Fact]
    public async Task Rename_ChangesName()
    {
        var calendar = await _service.CreateAsync(_alice, "Alt");

        await _service.RenameAsync(_alice, calendar.Id, "Neu");

        Assert.Equal("Neu", (await _service.GetAsync(_alice, calendar.Id))!.Name);
    }

    [Fact]
    public async Task RegenerateToken_ReplacesToken()
    {
        var calendar = await _service.CreateAsync(_alice, "K");

        var newToken = await _service.RegenerateTokenAsync(_alice, calendar.Id);

        Assert.NotEqual(calendar.Token, newToken);
        Assert.Equal(newToken, (await _service.GetAsync(_alice, calendar.Id))!.Token);
    }

    [Fact]
    public async Task AddSource_NormalizesUrlAndAppendsSortOrder()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        _fetcher.Set("https://example.com/w.ics", Calendar(Event("w@x")));

        var first = await _service.AddSourceAsync(_alice, calendar.Id, "Webcal", "webcal://example.com/w.ics");
        var second = await _service.AddSourceAsync(_alice, calendar.Id, "B", UrlB);

        Assert.Equal("https://example.com/w.ics", first.Url);
        Assert.True(second.SortOrder > first.SortOrder);
    }

    [Fact]
    public async Task AddSource_WithoutName_UsesHost()
    {
        var calendar = await _service.CreateAsync(_alice, "K");

        var source = await _service.AddSourceAsync(_alice, calendar.Id, "  ", UrlA);

        Assert.Equal("example.com", source.Name);
    }

    [Fact]
    public async Task AddSource_EnforcesSourceLimit()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        await _service.AddSourceAsync(_alice, calendar.Id, "A", UrlA);
        await _service.AddSourceAsync(_alice, calendar.Id, "B", UrlB);

        await Assert.ThrowsAsync<DomainException>(() => _service.AddSourceAsync(_alice, calendar.Id, "C", UrlA));
    }

    [Fact]
    public async Task AddSource_RejectsInvalidUrl()
    {
        var calendar = await _service.CreateAsync(_alice, "K");

        var ex = await Assert.ThrowsAsync<DomainException>(() => _service.AddSourceAsync(_alice, calendar.Id, "X", "ftp://x"));

        Assert.Equal("Ungültige URL", ex.Message);
    }

    [Fact]
    public async Task AddSource_RejectsUnreachableUrl()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        _fetcher.Fail("https://example.com/down.ics", "HTTP 500");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.AddSourceAsync(_alice, calendar.Id, "X", "https://example.com/down.ics"));

        Assert.Contains("HTTP 500", ex.Message);
    }

    [Fact]
    public async Task AddSource_RejectsHtmlResponse()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        _fetcher.Set("https://example.com/login", "<html><body>Bitte anmelden</body></html>");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _service.AddSourceAsync(_alice, calendar.Id, "X", "https://example.com/login"));

        Assert.Contains("keinen iCal-Kalender", ex.Message);
        Assert.Empty((await _service.GetAsync(_alice, calendar.Id))!.Sources);
    }

    [Fact]
    public async Task UpdateSource_ChangesNameAndUrl_AndRevalidates()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        var source = await _service.AddSourceAsync(_alice, calendar.Id, "A", UrlA);

        await _service.UpdateSourceAsync(_alice, source.Id, "Neu", UrlB);

        var updated = Assert.Single((await _service.GetAsync(_alice, calendar.Id))!.Sources);
        Assert.Equal("Neu", updated.Name);
        Assert.Equal(UrlB, updated.Url);

        await Assert.ThrowsAsync<DomainException>(() => _service.UpdateSourceAsync(_alice, source.Id, "X", "ftp://x"));
    }

    [Fact]
    public async Task SourceMutationsOnForeignSource_AreRejected()
    {
        var bobs = await _service.CreateAsync(_bob, "Bobs");
        var source = await _service.AddSourceAsync(_bob, bobs.Id, "A", UrlA);

        await Assert.ThrowsAsync<DomainException>(() => _service.UpdateSourceAsync(_alice, source.Id, "X", UrlB));
        await Assert.ThrowsAsync<DomainException>(() => _service.DeleteSourceAsync(_alice, source.Id));
        await Assert.ThrowsAsync<DomainException>(() => _service.MoveSourceAsync(_alice, source.Id, 1));
        Assert.Single((await _service.GetAsync(_bob, bobs.Id))!.Sources);
    }

    [Fact]
    public async Task DeleteSource_RemovesIt()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        var source = await _service.AddSourceAsync(_alice, calendar.Id, "A", UrlA);

        await _service.DeleteSourceAsync(_alice, source.Id);

        Assert.Empty((await _service.GetAsync(_alice, calendar.Id))!.Sources);
    }

    [Fact]
    public async Task MoveSource_SwapsWithNeighbourAndIgnoresBounds()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        var a = await _service.AddSourceAsync(_alice, calendar.Id, "A", UrlA);
        var b = await _service.AddSourceAsync(_alice, calendar.Id, "B", UrlB);

        await _service.MoveSourceAsync(_alice, b.Id, -1);
        Assert.Equal(new[] { "B", "A" }, (await _service.GetAsync(_alice, calendar.Id))!.Sources.Select(s => s.Name));

        await _service.MoveSourceAsync(_alice, b.Id, -1); // schon ganz oben: keine Änderung
        Assert.Equal(new[] { "B", "A" }, (await _service.GetAsync(_alice, calendar.Id))!.Sources.Select(s => s.Name));

        await _service.MoveSourceAsync(_alice, a.Id, 1); // schon ganz unten: keine Änderung
        Assert.Equal(new[] { "B", "A" }, (await _service.GetAsync(_alice, calendar.Id))!.Sources.Select(s => s.Name));
    }

    [Fact]
    public async Task DeleteCalendar_CascadesToSources()
    {
        var calendar = await _service.CreateAsync(_alice, "K");
        await _service.AddSourceAsync(_alice, calendar.Id, "A", UrlA);

        await _service.DeleteAsync(_alice, calendar.Id);

        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.Calendars.CountAsync());
        Assert.Equal(0, await db.Sources.CountAsync());
    }
}
```

- [ ] **Step 5: Tests laufen lassen, Fehlschlag prüfen**

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~CalendarServiceTests|FullyQualifiedName~MigrationsTests"`
Expected: `MigrationsTests` PASS (Migration aus Step 2), `CalendarServiceTests` Build-Fehler (`CalendarService` fehlt).

- [ ] **Step 6: Implementierung**

`Calendars/DomainException.cs`:

```csharp
namespace Webionic.ICalMerger.Calendars;

/// <summary>Fehler, deren (deutsche) Meldung direkt in der UI angezeigt werden darf.</summary>
public class DomainException(string message) : Exception(message);
```

`Calendars/AppLimits.cs`:

```csharp
namespace Webionic.ICalMerger.Calendars;

public sealed class AppLimits
{
    public int MaxCalendarsPerUser { get; set; } = 10;
    public int MaxSourcesPerCalendar { get; set; } = 20;
}
```

`Calendars/TokenGenerator.cs`:

```csharp
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace Webionic.ICalMerger.Calendars;

public static class TokenGenerator
{
    /// <summary>32 Zufallsbytes als Base64Url (43 Zeichen).</summary>
    public static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
}
```

`Calendars/CalendarService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Webionic.ICalMerger.Data;
using Webionic.ICalMerger.Fetching;
using Webionic.ICalMerger.Merging;

namespace Webionic.ICalMerger.Calendars;

public sealed class CalendarService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICalendarFetcher fetcher,
    IOptions<AppLimits> limits)
{
    private const string CalendarNotFound = "Kalender nicht gefunden.";
    private const string SourceNotFound = "Quelle nicht gefunden.";

    public async Task<List<MergedCalendar>> ListAsync(string ownerId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var calendars = await db.Calendars.AsNoTracking().Include(c => c.Sources)
            .Where(c => c.OwnerId == ownerId).OrderBy(c => c.Name).ThenBy(c => c.Id).ToListAsync();
        calendars.ForEach(SortSources);
        return calendars;
    }

    public async Task<MergedCalendar?> GetAsync(string ownerId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var calendar = await db.Calendars.AsNoTracking().Include(c => c.Sources)
            .FirstOrDefaultAsync(c => c.Id == id && c.OwnerId == ownerId);
        if (calendar is not null) SortSources(calendar);
        return calendar;
    }

    public async Task<MergedCalendar> CreateAsync(string ownerId, string name)
    {
        name = ValidateName(name);
        await using var db = await dbFactory.CreateDbContextAsync();

        var count = await db.Calendars.CountAsync(c => c.OwnerId == ownerId);
        if (count >= limits.Value.MaxCalendarsPerUser)
        {
            throw new DomainException($"Du kannst höchstens {limits.Value.MaxCalendarsPerUser} Kalender anlegen.");
        }

        var calendar = new MergedCalendar
        {
            OwnerId = ownerId,
            Name = name,
            Token = TokenGenerator.NewToken(),
            CreatedAt = DateTime.UtcNow,
        };
        db.Calendars.Add(calendar);
        await db.SaveChangesAsync();
        return calendar;
    }

    public async Task RenameAsync(string ownerId, int id, string name)
    {
        name = ValidateName(name);
        await using var db = await dbFactory.CreateDbContextAsync();
        var calendar = await FindOwnedCalendarAsync(db, ownerId, id);
        calendar.Name = name;
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string ownerId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var calendar = await FindOwnedCalendarAsync(db, ownerId, id);
        db.Calendars.Remove(calendar);
        await db.SaveChangesAsync();
    }

    public async Task<string> RegenerateTokenAsync(string ownerId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var calendar = await FindOwnedCalendarAsync(db, ownerId, id);
        calendar.Token = TokenGenerator.NewToken();
        await db.SaveChangesAsync();
        return calendar.Token;
    }

    public async Task<CalendarSource> AddSourceAsync(string ownerId, int calendarId, string name, string url, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var calendar = await FindOwnedCalendarAsync(db, ownerId, calendarId, ct);

        var existing = await db.Sources.Where(s => s.MergedCalendarId == calendarId).Select(s => s.SortOrder).ToListAsync(ct);
        if (existing.Count >= limits.Value.MaxSourcesPerCalendar)
        {
            throw new DomainException($"Ein Kalender kann höchstens {limits.Value.MaxSourcesPerCalendar} Quellen haben.");
        }

        var uri = await ValidateSourceAsync(url, ct);
        var source = new CalendarSource
        {
            MergedCalendarId = calendar.Id,
            Name = ResolveSourceName(name, uri),
            Url = uri.AbsoluteUri,
            SortOrder = existing.Count == 0 ? 0 : existing.Max() + 1,
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync(ct);
        return source;
    }

    public async Task UpdateSourceAsync(string ownerId, int sourceId, string name, string url, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var source = await FindOwnedSourceAsync(db, ownerId, sourceId, ct);

        var uri = await ValidateSourceAsync(url, ct);
        source.Name = ResolveSourceName(name, uri);
        source.Url = uri.AbsoluteUri;
        source.LastAttemptAt = null;
        source.LastSuccessAt = null;
        source.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteSourceAsync(string ownerId, int sourceId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var source = await FindOwnedSourceAsync(db, ownerId, sourceId);
        db.Sources.Remove(source);
        await db.SaveChangesAsync();
    }

    /// <param name="direction">-1 nach oben (höhere Priorität), +1 nach unten.</param>
    public async Task MoveSourceAsync(string ownerId, int sourceId, int direction)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var source = await FindOwnedSourceAsync(db, ownerId, sourceId);

        var siblings = await db.Sources.Where(s => s.MergedCalendarId == source.MergedCalendarId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync();
        var index = siblings.FindIndex(s => s.Id == source.Id);
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= siblings.Count) return;

        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        for (var i = 0; i < siblings.Count; i++) siblings[i].SortOrder = i;
        await db.SaveChangesAsync();
    }

    private async Task<Uri> ValidateSourceAsync(string url, CancellationToken ct)
    {
        if ((url ?? "").Length > 2000) throw new DomainException("Die URL ist zu lang.");

        Uri uri;
        try
        {
            uri = SafeHttpFetcher.NormalizeUrl(url!);
        }
        catch (FetchException ex)
        {
            throw new DomainException(ex.Message);
        }

        string ics;
        try
        {
            ics = await fetcher.FetchAsync(uri.AbsoluteUri, ct);
        }
        catch (FetchException ex)
        {
            throw new DomainException($"Die URL konnte nicht abgerufen werden: {ex.Message}");
        }

        if (!IcsMerger.LooksLikeCalendar(ics))
        {
            throw new DomainException("Die URL liefert keinen iCal-Kalender.");
        }

        return uri;
    }

    private static string ResolveSourceName(string? name, Uri uri)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) trimmed = uri.Host;
        if (trimmed.Length > 100) throw new DomainException("Der Name ist zu lang (höchstens 100 Zeichen).");
        return trimmed;
    }

    private static string ValidateName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) throw new DomainException("Bitte einen Namen eingeben.");
        if (trimmed.Length > 100) throw new DomainException("Der Name ist zu lang (höchstens 100 Zeichen).");
        return trimmed;
    }

    private static void SortSources(MergedCalendar calendar) =>
        calendar.Sources = calendar.Sources.OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToList();

    private static async Task<MergedCalendar> FindOwnedCalendarAsync(ApplicationDbContext db, string ownerId, int id, CancellationToken ct = default) =>
        await db.Calendars.FirstOrDefaultAsync(c => c.Id == id && c.OwnerId == ownerId, ct)
        ?? throw new DomainException(CalendarNotFound);

    private static async Task<CalendarSource> FindOwnedSourceAsync(ApplicationDbContext db, string ownerId, int id, CancellationToken ct = default) =>
        await db.Sources.FirstOrDefaultAsync(s => s.Id == id && s.MergedCalendar!.OwnerId == ownerId, ct)
        ?? throw new DomainException(SourceNotFound);
}
```

In `ServiceRegistration.AddCalendarServices` ergänzen (vor `return services;`):

```csharp
        services.Configure<AppLimits>(configuration.GetSection("Limits"));
        services.AddScoped<CalendarService>();
```

und oben `using Webionic.ICalMerger.Calendars;`.

- [ ] **Step 7: Tests laufen lassen**

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~CalendarServiceTests|FullyQualifiedName~MigrationsTests"`
Expected: alle PASS. `HasPendingModelChanges` fehlt im EF-Paket? Dann ersetze die Zeile durch einen Vergleich des Modells über `db.Database.GetPendingMigrations()` (leer) plus `EnsureCreated`-freien Zugriff auf beide Tabellen, aber lass die Prüfung nicht ersatzlos weg.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: add calendar data model, migration and CalendarService" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: `SourceCache` und `FeedService`

**Files:**
- Create: `src/Webionic.ICalMerger/Feed/SourceCache.cs`, `src/Webionic.ICalMerger/Feed/FeedService.cs`
- Modify: `src/Webionic.ICalMerger/ServiceRegistration.cs`
- Test: `tests/Webionic.ICalMerger.Tests/Feed/SourceCacheTests.cs`, `tests/Webionic.ICalMerger.Tests/Feed/FeedServiceTests.cs`

**Interfaces:**
- Consumes: `ICalendarFetcher`, `FetchException`, `IcsMerger`, `ApplicationDbContext`, `TestDb`, `FakeFetcher`, `IcsFixtures`.
- Produces:
  - `sealed record SourceResult(string? Ics, string? Error, bool Stale)`
  - `interface ISourceCache { Task<SourceResult> GetAsync(string url, CancellationToken ct); }`
  - `sealed record SourceCacheOptions(TimeSpan Ttl, TimeSpan FailureBackoff)` mit `SourceCacheOptions.Default` (5 min / 1 min)
  - `sealed class SourceCache(ICalendarFetcher fetcher, TimeProvider time, SourceCacheOptions? options = null) : ISourceCache`
  - `sealed record FeedResult(string? Ics, int FailedSources)`
  - `sealed class FeedService(IDbContextFactory<ApplicationDbContext>, ISourceCache, TimeProvider)` mit `Task<FeedResult?> BuildAsync(string token, CancellationToken ct)`. `null` heißt unbekanntes Token. `Ics == null` heißt, alle Quellen sind ausgefallen und es gibt keinen Cache.

- [ ] **Step 1: Failing Tests für `SourceCache`**

`tests/Webionic.ICalMerger.Tests/Feed/SourceCacheTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using Webionic.ICalMerger.Feed;
using Webionic.ICalMerger.Tests.Support;
using static Webionic.ICalMerger.Tests.Support.IcsFixtures;

namespace Webionic.ICalMerger.Tests.Feed;

public class SourceCacheTests
{
    private const string Url = "https://example.com/a.ics";

    private readonly FakeFetcher _fetcher = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly SourceCache _cache;

    public SourceCacheTests()
    {
        _cache = new SourceCache(_fetcher, _time);
    }

    [Fact]
    public async Task Get_FetchesOnceWithinTtl()
    {
        _fetcher.Set(Url, Calendar(Event("a@x")));

        var first = await _cache.GetAsync(Url, default);
        _time.Advance(TimeSpan.FromMinutes(4));
        var second = await _cache.GetAsync(Url, default);

        Assert.Equal(1, _fetcher.CallCount(Url));
        Assert.Equal(first.Ics, second.Ics);
        Assert.Null(second.Error);
        Assert.False(second.Stale);
    }

    [Fact]
    public async Task Get_RefetchesAfterTtl()
    {
        _fetcher.Set(Url, Calendar(Event("a@x")));
        await _cache.GetAsync(Url, default);

        _time.Advance(TimeSpan.FromMinutes(6));
        _fetcher.Set(Url, Calendar(Event("neu@x")));
        var result = await _cache.GetAsync(Url, default);

        Assert.Equal(2, _fetcher.CallCount(Url));
        Assert.Contains("neu@x", result.Ics);
    }

    [Fact]
    public async Task Get_FailureWithoutCache_ReturnsErrorOnly()
    {
        _fetcher.Fail(Url, "HTTP 500");

        var result = await _cache.GetAsync(Url, default);

        Assert.Null(result.Ics);
        Assert.Equal("HTTP 500", result.Error);
        Assert.False(result.Stale);
    }

    [Fact]
    public async Task Get_FailureAfterSuccess_ServesStaleData()
    {
        _fetcher.Set(Url, Calendar(Event("alt@x")));
        await _cache.GetAsync(Url, default);

        _time.Advance(TimeSpan.FromMinutes(6));
        _fetcher.Fail(Url, "HTTP 503");
        var result = await _cache.GetAsync(Url, default);

        Assert.Contains("alt@x", result.Ics);
        Assert.Equal("HTTP 503", result.Error);
        Assert.True(result.Stale);
    }

    [Fact]
    public async Task Get_DoesNotHammerFailingSource()
    {
        _fetcher.Fail(Url);

        await _cache.GetAsync(Url, default);
        _time.Advance(TimeSpan.FromSeconds(30));
        await _cache.GetAsync(Url, default);
        Assert.Equal(1, _fetcher.CallCount(Url));

        _time.Advance(TimeSpan.FromSeconds(31));
        await _cache.GetAsync(Url, default);
        Assert.Equal(2, _fetcher.CallCount(Url));
    }

    [Fact]
    public async Task Get_RecoversAfterFailure()
    {
        _fetcher.Fail(Url);
        await _cache.GetAsync(Url, default);

        _time.Advance(TimeSpan.FromMinutes(2));
        _fetcher.Set(Url, Calendar(Event("ok@x")));
        var result = await _cache.GetAsync(Url, default);

        Assert.Contains("ok@x", result.Ics);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Get_TreatsHtmlResponseAsFailureAndDoesNotCacheIt()
    {
        _fetcher.Set(Url, "<html><body>Bitte anmelden</body></html>");

        var result = await _cache.GetAsync(Url, default);

        Assert.Null(result.Ics);
        Assert.Equal("Keine gültige iCal-Datei", result.Error);
    }

    [Fact]
    public async Task Get_HtmlAfterGoodData_KeepsServingGoodData()
    {
        _fetcher.Set(Url, Calendar(Event("gut@x")));
        await _cache.GetAsync(Url, default);

        _time.Advance(TimeSpan.FromMinutes(6));
        _fetcher.Set(Url, "<html>Captive Portal</html>");
        var result = await _cache.GetAsync(Url, default);

        Assert.Contains("gut@x", result.Ics);
        Assert.True(result.Stale);
    }

    [Fact]
    public async Task Get_CoalescesConcurrentRequestsForSameUrl()
    {
        var release = new TaskCompletionSource();
        _fetcher.SetAsync(Url, async _ =>
        {
            await release.Task;
            return Calendar(Event("a@x"));
        });

        var tasks = Enumerable.Range(0, 5).Select(_ => _cache.GetAsync(Url, default)).ToArray();
        await Task.Delay(100);
        release.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, _fetcher.CallCount(Url));
        Assert.All(results, r => Assert.Contains("a@x", r.Ics));
    }

    [Fact]
    public async Task Get_KeepsUrlsIndependent()
    {
        const string other = "https://example.com/b.ics";
        _fetcher.Set(Url, Calendar(Event("a@x")));
        _fetcher.Fail(other);

        var good = await _cache.GetAsync(Url, default);
        var bad = await _cache.GetAsync(other, default);

        Assert.NotNull(good.Ics);
        Assert.Null(bad.Ics);
    }
}
```

- [ ] **Step 2: `SourceCache` implementieren**

`src/Webionic.ICalMerger/Feed/SourceCache.cs`:

```csharp
using System.Collections.Concurrent;
using Webionic.ICalMerger.Fetching;
using Webionic.ICalMerger.Merging;

namespace Webionic.ICalMerger.Feed;

/// <param name="Ics">Letzter bekannter gültiger Inhalt, sonst null.</param>
/// <param name="Error">Fehler des letzten Abrufs, sonst null.</param>
/// <param name="Stale">True, wenn Ics veraltet ist, weil der letzte Abruf fehlschlug.</param>
public sealed record SourceResult(string? Ics, string? Error, bool Stale);

public interface ISourceCache
{
    Task<SourceResult> GetAsync(string url, CancellationToken ct);
}

public sealed record SourceCacheOptions(TimeSpan Ttl, TimeSpan FailureBackoff)
{
    public static SourceCacheOptions Default { get; } = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
}

public sealed class SourceCache(ICalendarFetcher fetcher, TimeProvider time, SourceCacheOptions? options = null) : ISourceCache
{
    private readonly SourceCacheOptions _options = options ?? SourceCacheOptions.Default;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<SourceResult> GetAsync(string url, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(url, _ => new Entry());

        // Ein gleichzeitiger Abruf pro URL: Wartende sehen danach den frischen Eintrag.
        await entry.Gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();

            if (entry.LastError is null && entry.Ics is not null && now - entry.FetchedAt < _options.Ttl)
            {
                return new SourceResult(entry.Ics, null, false);
            }

            if (entry.LastError is not null && now - entry.FailedAt < _options.FailureBackoff)
            {
                return Failed(entry);
            }

            try
            {
                var ics = await fetcher.FetchAsync(url, ct);
                if (!IcsMerger.LooksLikeCalendar(ics))
                {
                    throw new FetchException("Keine gültige iCal-Datei");
                }

                entry.Ics = ics;
                entry.FetchedAt = now;
                entry.LastError = null;
                return new SourceResult(ics, null, false);
            }
            catch (FetchException ex)
            {
                entry.LastError = ex.Message;
                entry.FailedAt = now;
                return Failed(entry);
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static SourceResult Failed(Entry entry) => new(entry.Ics, entry.LastError, entry.Ics is not null);

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? Ics { get; set; }
        public DateTimeOffset FetchedAt { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset FailedAt { get; set; }
    }
}
```

- [ ] **Step 3: Failing Tests für `FeedService`**

`tests/Webionic.ICalMerger.Tests/Feed/FeedServiceTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Webionic.ICalMerger.Calendars;
using Webionic.ICalMerger.Feed;
using Webionic.ICalMerger.Tests.Support;
using static Webionic.ICalMerger.Tests.Support.IcsFixtures;

namespace Webionic.ICalMerger.Tests.Feed;

public sealed class FeedServiceTests : IDisposable
{
    private const string UrlA = "https://example.com/a.ics";
    private const string UrlB = "https://example.com/b.ics";

    private readonly TestDb _db = new();
    private readonly FakeFetcher _fetcher = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly CalendarService _calendars;
    private readonly FeedService _feed;
    private string _owner = "";

    public FeedServiceTests()
    {
        _fetcher.Set(UrlA, Calendar(Event("a@x", "Von A")));
        _fetcher.Set(UrlB, Calendar(Event("b@x", "Von B")));
        _calendars = new CalendarService(_db, _fetcher, Options.Create(new AppLimits()));
        _feed = new FeedService(_db, new SourceCache(_fetcher, _time), _time);
    }

    public void Dispose() => _db.Dispose();

    private async Task<Data.MergedCalendar> CalendarWithSourcesAsync(params string[] urls)
    {
        _owner = await _db.AddUserAsync("owner@example.com");
        var calendar = await _calendars.CreateAsync(_owner, "Familie");
        foreach (var url in urls)
        {
            await _calendars.AddSourceAsync(_owner, calendar.Id, url, url);
        }
        return calendar;
    }

    [Fact]
    public async Task Build_UnknownToken_ReturnsNull()
    {
        Assert.Null(await _feed.BuildAsync("gibt-es-nicht", default));
    }

    [Fact]
    public async Task Build_MergesAllSources()
    {
        var calendar = await CalendarWithSourcesAsync(UrlA, UrlB);

        var result = await _feed.BuildAsync(calendar.Token, default);

        Assert.NotNull(result?.Ics);
        Assert.Contains("UID:a@x", result.Ics);
        Assert.Contains("UID:b@x", result.Ics);
        Assert.Contains("X-WR-CALNAME:Familie", result.Ics);
        Assert.Equal(0, result.FailedSources);
    }

    [Fact]
    public async Task Build_UsesSortOrderAsPriorityForDuplicates()
    {
        _fetcher.Set(UrlA, Calendar(Event("dup@x", "Aus A")));
        _fetcher.Set(UrlB, Calendar(Event("dup@x", "Aus B")));
        var calendar = await CalendarWithSourcesAsync(UrlA, UrlB);
        var sourceB = (await _calendars.GetAsync(_owner, calendar.Id))!.Sources[1];
        await _calendars.MoveSourceAsync(_owner, sourceB.Id, -1);

        var result = await _feed.BuildAsync(calendar.Token, default);

        Assert.Contains("SUMMARY:Aus B", result!.Ics);
        Assert.DoesNotContain("SUMMARY:Aus A", result.Ics);
    }

    [Fact]
    public async Task Build_PartialFailure_ReturnsRestAndStoresStatus()
    {
        var calendar = await CalendarWithSourcesAsync(UrlA, UrlB);
        _fetcher.Fail(UrlB, "HTTP 500");

        var result = await _feed.BuildAsync(calendar.Token, default);

        Assert.Contains("UID:a@x", result!.Ics);
        Assert.DoesNotContain("UID:b@x", result.Ics);
        Assert.Equal(1, result.FailedSources);

        var sources = (await _calendars.GetAsync(_owner, calendar.Id))!.Sources;
        Assert.Null(sources[0].LastError);
        Assert.NotNull(sources[0].LastSuccessAt);
        Assert.Equal("HTTP 500", sources[1].LastError);
        Assert.Null(sources[1].LastSuccessAt);
        Assert.NotNull(sources[1].LastAttemptAt);
    }

    [Fact]
    public async Task Build_AllSourcesFail_ReturnsNullIcs()
    {
        var calendar = await CalendarWithSourcesAsync(UrlA, UrlB);
        _fetcher.Fail(UrlA);
        _fetcher.Fail(UrlB);

        var result = await _feed.BuildAsync(calendar.Token, default);

        Assert.NotNull(result);
        Assert.Null(result.Ics);
        Assert.Equal(2, result.FailedSources);
    }

    [Fact]
    public async Task Build_NoSources_ReturnsEmptyValidCalendar()
    {
        var calendar = await CalendarWithSourcesAsync();

        var result = await _feed.BuildAsync(calendar.Token, default);

        Assert.StartsWith("BEGIN:VCALENDAR", result!.Ics);
        Assert.EndsWith("END:VCALENDAR\r\n", result.Ics);
        Assert.DoesNotContain("BEGIN:VEVENT", result.Ics);
    }

    [Fact]
    public async Task Build_AfterSourceGoesDown_StillServesLastKnownData()
    {
        var calendar = await CalendarWithSourcesAsync(UrlA);
        await _feed.BuildAsync(calendar.Token, default);

        _time.Advance(TimeSpan.FromMinutes(6));
        _fetcher.Fail(UrlA, "HTTP 503");
        var result = await _feed.BuildAsync(calendar.Token, default);

        Assert.Contains("UID:a@x", result!.Ics);
        var source = Assert.Single((await _calendars.GetAsync(_owner, calendar.Id))!.Sources);
        Assert.Equal("HTTP 503", source.LastError);
    }
}
```

- [ ] **Step 4: `FeedService` implementieren**

`src/Webionic.ICalMerger/Feed/FeedService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Webionic.ICalMerger.Data;
using Webionic.ICalMerger.Merging;

namespace Webionic.ICalMerger.Feed;

/// <param name="Ics">Fertiger Kalender. Null, wenn alle Quellen ausgefallen sind und kein Cache existiert.</param>
public sealed record FeedResult(string? Ics, int FailedSources);

public sealed class FeedService(IDbContextFactory<ApplicationDbContext> dbFactory, ISourceCache cache, TimeProvider time)
{
    /// <returns>Null, wenn das Token unbekannt ist.</returns>
    public async Task<FeedResult?> BuildAsync(string token, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var calendar = await db.Calendars.Include(c => c.Sources).FirstOrDefaultAsync(c => c.Token == token, ct);
        if (calendar is null) return null;

        var sources = calendar.Sources.OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToList();
        var results = await Task.WhenAll(sources.Select(s => cache.GetAsync(s.Url, ct)));

        var now = time.GetUtcNow().UtcDateTime;
        for (var i = 0; i < sources.Count; i++)
        {
            sources[i].LastAttemptAt = now;
            if (results[i].Error is null)
            {
                sources[i].LastSuccessAt = now;
                sources[i].LastError = null;
            }
            else
            {
                var error = results[i].Error!;
                sources[i].LastError = error.Length > 500 ? error[..500] : error;
            }
        }
        await db.SaveChangesAsync(ct);

        var available = results.Where(r => r.Ics is not null).Select(r => r.Ics!).ToList();
        var failed = results.Count(r => r.Error is not null);

        if (sources.Count > 0 && available.Count == 0)
        {
            return new FeedResult(null, failed);
        }

        return new FeedResult(IcsMerger.Merge(calendar.Name, available), failed);
    }
}
```

- [ ] **Step 5: Registrierung ergänzen**

In `ServiceRegistration.AddCalendarServices` vor `return services;`:

```csharp
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISourceCache, SourceCache>();
        services.AddScoped<FeedService>();
```

und oben `using Webionic.ICalMerger.Feed;`.

- [ ] **Step 6: Tests laufen lassen**

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~Feed"`
Expected: alle PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add source cache and feed service" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```


### Task 6: Feed-Endpunkt und Integrationstests

**Files:**
- Create: `src/Webionic.ICalMerger/Feed/FeedEndpoint.cs`
- Modify: `src/Webionic.ICalMerger/Program.cs`
- Create: `tests/Webionic.ICalMerger.Tests/Support/AppFactory.cs`
- Test: `tests/Webionic.ICalMerger.Tests/Feed/FeedEndpointTests.cs`

**Interfaces:**
- Consumes: `FeedService.BuildAsync`, `CalendarService`, `FakeFetcher`, `public partial class Program`.
- Produces:
  - `static IEndpointRouteBuilder FeedEndpoint.MapFeedEndpoint(this IEndpointRouteBuilder endpoints)` für `GET /feed/{token}.ics`.
  - Testhelfer `AppFactory : WebApplicationFactory<Program>` mit `FakeFetcher Fetcher`, `const string AdminEmail`/`AdminPassword`, `Task<MergedCalendar> SeedCalendarAsync(string name, params string[] sourceUrls)` (legt einen eigenen Nutzer, einen Kalender und die Quellen an; die Quell-URLs müssen vorher in `Fetcher` stehen).

- [ ] **Step 1: `AppFactory` anlegen**

`tests/Webionic.ICalMerger.Tests/Support/AppFactory.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
```

- [ ] **Step 2: Failing Tests**

`tests/Webionic.ICalMerger.Tests/Feed/FeedEndpointTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Webionic.ICalMerger.Calendars;
using Webionic.ICalMerger.Tests.Support;
using static Webionic.ICalMerger.Tests.Support.IcsFixtures;

namespace Webionic.ICalMerger.Tests.Feed;

public sealed class FeedEndpointTests : IDisposable
{
    private const string UrlA = "https://example.com/a.ics";
    private const string UrlB = "https://example.com/b.ics";

    private readonly AppFactory _factory = new();

    public FeedEndpointTests()
    {
        _factory.Fetcher.Set(UrlA, Calendar(Event("a@x", "Von A")));
        _factory.Fetcher.Set(UrlB, Calendar(Event("b@x", "Von B")));
    }

    public void Dispose() => _factory.Dispose();

    private static string FeedPath(string token) => $"/feed/{token}.ics";

    [Fact]
    public async Task Feed_ReturnsMergedCalendarWithHeaders()
    {
        var calendar = await _factory.SeedCalendarAsync("Familie", UrlA, UrlB);

        var response = await _factory.CreateClient().GetAsync(FeedPath(calendar.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("public, max-age=300", response.Headers.CacheControl!.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", body);
        Assert.Contains("UID:a@x", body);
        Assert.Contains("UID:b@x", body);
        Assert.Contains("X-WR-CALNAME:Familie", body);
    }

    [Fact]
    public async Task Feed_UnknownToken_Returns404()
    {
        var response = await _factory.CreateClient().GetAsync(FeedPath(TokenGenerator.NewToken()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("..")]
    [InlineData("%2e%2e%2fetc%2fpasswd")]
    [InlineData("a%20b")]
    [InlineData("%27%20OR%201%3D1--")]
    public async Task Feed_GarbageToken_Returns404(string token)
    {
        var response = await _factory.CreateClient().GetAsync(FeedPath(token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Feed_EmptyOrHugeToken_Returns404()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/feed/.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(FeedPath(new string('a', 5000)))).StatusCode);
    }

    [Fact]
    public async Task Feed_TokenWithWrongCase_Returns404()
    {
        var calendar = await _factory.SeedCalendarAsync("K", UrlA);
        var swapped = new string(calendar.Token.Select(c => char.IsLower(c) ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)).ToArray());
        Assert.NotEqual(calendar.Token, swapped);

        var response = await _factory.CreateClient().GetAsync(FeedPath(swapped));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Feed_AllSourcesDown_Returns503()
    {
        var calendar = await _factory.SeedCalendarAsync("K", UrlA, UrlB);
        _factory.Fetcher.Fail(UrlA);
        _factory.Fetcher.Fail(UrlB);

        var response = await _factory.CreateClient().GetAsync(FeedPath(calendar.Token));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Feed_PartialFailure_Returns200WithRemainingSources()
    {
        var calendar = await _factory.SeedCalendarAsync("K", UrlA, UrlB);
        _factory.Fetcher.Fail(UrlB);

        var response = await _factory.CreateClient().GetAsync(FeedPath(calendar.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("UID:a@x", body);
        Assert.DoesNotContain("UID:b@x", body);
    }

    [Fact]
    public async Task Feed_WithoutSources_ReturnsEmptyValidCalendar()
    {
        var calendar = await _factory.SeedCalendarAsync("Leer");

        var response = await _factory.CreateClient().GetAsync(FeedPath(calendar.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("BEGIN:VCALENDAR", body);
        Assert.DoesNotContain("BEGIN:VEVENT", body);
    }

    [Fact]
    public async Task Feed_AfterTokenRegeneration_OldUrlIsGone()
    {
        var calendar = await _factory.SeedCalendarAsync("K", UrlA);
        string newToken;
        using (var scope = _factory.Services.CreateScope())
        {
            newToken = await scope.ServiceProvider.GetRequiredService<CalendarService>()
                .RegenerateTokenAsync(calendar.OwnerId, calendar.Id);
        }
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(FeedPath(calendar.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(FeedPath(newToken))).StatusCode);
    }
}
```

- [ ] **Step 3: Tests laufen lassen, Fehlschlag prüfen**

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~FeedEndpointTests"`
Expected: FAIL. Der Endpunkt existiert noch nicht, die Antworten sind `404`, sodass u. a. `Feed_ReturnsMergedCalendarWithHeaders` fehlschlägt.

Hinweis: Meldet `WebApplicationFactory` „solution file not found“ oder findet den Content-Root nicht (z. B. wegen `.slnx`), setze in `AppFactory.ConfigureWebHost` zusätzlich `builder.UseContentRoot(<absoluter Pfad zu src/Webionic.ICalMerger>)`, abgeleitet über `AppContext.BaseDirectory` nach oben bis zum Ordner mit `Webionic.iCalMerger.*`.

- [ ] **Step 4: Endpunkt implementieren**

`src/Webionic.ICalMerger/Feed/FeedEndpoint.cs`:

```csharp
namespace Webionic.ICalMerger.Feed;

public static class FeedEndpoint
{
    private const int MinTokenLength = 16;
    private const int MaxTokenLength = 128;

    public static IEndpointRouteBuilder MapFeedEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/feed/{token}.ics", async (string token, FeedService feed, HttpContext http, CancellationToken ct) =>
        {
            // Offensichtlichen Müll abweisen, bevor die Datenbank gefragt wird.
            if (token.Length is < MinTokenLength or > MaxTokenLength)
            {
                return Results.NotFound();
            }

            var result = await feed.BuildAsync(token, ct);
            if (result is null)
            {
                return Results.NotFound();
            }

            if (result.Ics is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            http.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Text(result.Ics, "text/calendar; charset=utf-8");
        }).AllowAnonymous();

        return endpoints;
    }
}
```

In `Program.cs` direkt vor `app.MapStaticAssets();` ergänzen:

```csharp
app.MapFeedEndpoint();
```

und `using Webionic.ICalMerger.Feed;` oben.

- [ ] **Step 5: Tests laufen lassen**

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~FeedEndpointTests"`
Expected: alle PASS. Liefert der Test `Feed_ReturnsMergedCalendarWithHeaders` einen abweichenden `Content-Type` (z. B. ohne Leerzeichen), passe den Test an den tatsächlichen, gleichwertigen Header an, nicht umgekehrt.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add public feed endpoint" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Identity-Verwaltung, Bootstrap-Admin und Account-Flows

**Files:**
- Create: `src/Webionic.ICalMerger/Users/Roles.cs`, `Users/IdentityRegistration.cs`, `Users/AdminBootstrapper.cs`, `Users/UserAdminService.cs`
- Modify: `src/Webionic.ICalMerger/Program.cs`, `src/Webionic.ICalMerger/Components/Account/IdentityRevalidatingAuthenticationStateProvider.cs`
- Create: `tests/Webionic.ICalMerger.Tests/Support/WebHelpers.cs`
- Test: `tests/Webionic.ICalMerger.Tests/Users/UserAdminServiceTests.cs`, `tests/Webionic.ICalMerger.Tests/Users/AccountFlowTests.cs`

**Interfaces:**
- Consumes: `ApplicationDbContext`, `ApplicationUser`, `DomainException`, `AppFactory`, Seiten `/Account/Login`, `/Account/ResetPassword`.
- Produces:
  - `static class Roles { const string Admin = "Admin"; }`
  - `static IServiceCollection IdentityRegistration.AddAppIdentity(this IServiceCollection services)`
  - `static Task AdminBootstrapper.EnsureAdminAsync(IServiceProvider services, IConfiguration config)`
  - `sealed record UserRow(string Id, string Email, bool IsAdmin, bool IsLockedOut, bool HasPassword)`
  - `sealed class UserAdminService(IServiceScopeFactory scopes)` (Singleton) mit `ListAsync()`, `InviteAsync(string email, string baseUri)` → Link, `CreateResetLinkAsync(string userId, string baseUri)` → Link, `SetLockedAsync(string userId, bool locked)`, `SetAdminAsync(string userId, bool isAdmin)`, `DeleteAsync(string userId, string actingUserId)`. Fehler sind `DomainException` mit deutscher Meldung.
  - Testhelfer `WebHelpers.NewClient(this AppFactory)` (ohne Auto-Redirect), `WebHelpers.PostFormAsync(client, path, handler, fields)`, `WebHelpers.LoginAsync(client, email, password)`.

- [ ] **Step 1: Rollen und Identity-Registrierung**

`Users/Roles.cs`:

```csharp
namespace Webionic.ICalMerger.Users;

public static class Roles
{
    public const string Admin = "Admin";
}
```

`Users/IdentityRegistration.cs`:

```csharp
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
            .AddDefaultTokenProviders();

        // Einladungs- und Reset-Links sind 7 Tage gültig.
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromDays(7));

        // Sperren und Rollenwechsel sollen zeitnah greifen.
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

        return services;
    }
}
```

In `Program.cs` ersetze den gesamten Block `builder.Services.AddIdentityCore<ApplicationUser>(options => { ... }) ... .AddDefaultTokenProviders();` durch:

```csharp
builder.Services.AddAppIdentity();
builder.Services.AddSingleton<UserAdminService>();
```

und ergänze `using Webionic.ICalMerger.Users;`. Nach dem Migrations-Block (`using (var scope = ...) { ... }`) ergänzen:

```csharp
await AdminBootstrapper.EnsureAdminAsync(app.Services, app.Configuration);
```

In `Components/Account/IdentityRevalidatingAuthenticationStateProvider.cs` ersetze in `RevalidationInterval` die `TimeSpan.FromMinutes(30)` durch `TimeSpan.FromMinutes(1)` (Prüfe mit `grep -n "FromMinutes" src/Webionic.ICalMerger/Components/Account/IdentityRevalidatingAuthenticationStateProvider.cs`).

- [ ] **Step 2: Test-Helfer für Web-Flows**

`tests/Webionic.ICalMerger.Tests/Support/WebHelpers.cs`:

```csharp
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Webionic.ICalMerger.Tests.Support;

internal static partial class WebHelpers
{
    [GeneratedRegex("<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryPattern();

    /// <summary>Client ohne automatisches Folgen von Weiterleitungen (Cookies werden gehalten).</summary>
    public static HttpClient NewClient(this AppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public static string AntiforgeryToken(string html)
    {
        var match = AntiforgeryPattern().Match(html);
        Assert.True(match.Success, "Kein Antiforgery-Token im HTML gefunden.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>Lädt die Seite (für Cookie und Token) und sendet dann das Formular mit dem angegebenen Handler-Namen.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string pathAndQuery, string handler, Dictionary<string, string> fields)
    {
        var page = await client.GetStringAsync(pathAndQuery);
        fields["__RequestVerificationToken"] = AntiforgeryToken(page);
        fields["_handler"] = handler;
        return await client.PostAsync(pathAndQuery, new FormUrlEncodedContent(fields));
    }

    public static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        PostFormAsync(client, "/Account/Login", "login", new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.RememberMe"] = "false",
        });
}
```

- [ ] **Step 3: Failing Tests für `UserAdminService` und Bootstrap**

`tests/Webionic.ICalMerger.Tests/Users/UserAdminServiceTests.cs`:

```csharp
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
        var link = await _admin.InviteAsync("  neu@example.com ", BaseUri);

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

    [Fact]
    public async Task Invite_LinkWorksOnlyOnce()
    {
        var token = TokenFromLink(await _admin.InviteAsync("neu@example.com", BaseUri));

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
        await _admin.InviteAsync("neu@example.com", BaseUri);

        var ex = await Assert.ThrowsAsync<DomainException>(() => _admin.InviteAsync("Neu@Example.com", BaseUri));

        Assert.Contains("bereits vergeben", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kein-at-zeichen")]
    public async Task Invite_RejectsInvalidEmail(string email)
    {
        await Assert.ThrowsAsync<DomainException>(() => _admin.InviteAsync(email, BaseUri));
    }

    [Fact]
    public async Task ResetLink_ReplacesExistingPassword()
    {
        var id = await SeedAsync("alt@example.com", admin: false);

        var token = TokenFromLink(await _admin.CreateResetLinkAsync(id, BaseUri));

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
        await Assert.ThrowsAsync<DomainException>(() => _admin.CreateResetLinkAsync("gibt-es-nicht", BaseUri));
    }

    [Fact]
    public async Task Delete_Self_IsRejected()
    {
        var first = await SeedAsync("eins@example.com", admin: true);
        await SeedAsync("zwei@example.com", admin: true);

        await Assert.ThrowsAsync<DomainException>(() => _admin.DeleteAsync(first, actingUserId: first));
    }

    [Fact]
    public async Task LastActiveAdmin_CannotBeDeletedLockedOrDemoted()
    {
        var onlyAdmin = await SeedAsync("admin@example.com", admin: true);
        var other = await SeedAsync("user@example.com", admin: false);

        await Assert.ThrowsAsync<DomainException>(() => _admin.DeleteAsync(onlyAdmin, actingUserId: other));
        await Assert.ThrowsAsync<DomainException>(() => _admin.SetLockedAsync(onlyAdmin, true));
        await Assert.ThrowsAsync<DomainException>(() => _admin.SetAdminAsync(onlyAdmin, false));

        var rows = await _admin.ListAsync();
        Assert.Contains(rows, r => r.Id == onlyAdmin && r.IsAdmin && !r.IsLockedOut);
    }

    [Fact]
    public async Task WithSecondAdmin_FirstCanBeDemotedAndDeleted()
    {
        var first = await SeedAsync("eins@example.com", admin: true);
        var second = await SeedAsync("zwei@example.com", admin: true);

        await _admin.SetAdminAsync(first, false);
        Assert.DoesNotContain(await _admin.ListAsync(), r => r.Id == first && r.IsAdmin);

        await _admin.DeleteAsync(first, actingUserId: second);
        Assert.DoesNotContain(await _admin.ListAsync(), r => r.Id == first);
    }

    [Fact]
    public async Task LockedAdmin_DoesNotCountAsActive()
    {
        var first = await SeedAsync("eins@example.com", admin: true);
        var second = await SeedAsync("zwei@example.com", admin: true);
        await _admin.SetLockedAsync(first, true);

        await Assert.ThrowsAsync<DomainException>(() => _admin.SetAdminAsync(second, false));
    }

    [Fact]
    public async Task SetLocked_LocksAndUnlocks()
    {
        var id = await SeedAsync("user@example.com", admin: false);

        await _admin.SetLockedAsync(id, true);
        Assert.True((await _admin.ListAsync()).Single().IsLockedOut);

        await _admin.SetLockedAsync(id, false);
        Assert.False((await _admin.ListAsync()).Single().IsLockedOut);
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
        await _admin.InviteAsync("neu@example.com", BaseUri);

        var rows = await _admin.ListAsync();

        var admin = rows.Single(r => r.Email == "admin@example.com");
        var invited = rows.Single(r => r.Email == "neu@example.com");
        Assert.True(admin.IsAdmin);
        Assert.True(admin.HasPassword);
        Assert.False(invited.IsAdmin);
        Assert.False(invited.HasPassword);
    }

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
}
```

- [ ] **Step 4: Failing Web-Flow-Tests**

`tests/Webionic.ICalMerger.Tests/Users/AccountFlowTests.cs`:

```csharp
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
```

- [ ] **Step 5: Tests laufen lassen, Fehlschlag prüfen**

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~Users"`
Expected: Build-Fehler (`AdminBootstrapper`, `UserAdminService` fehlen).

- [ ] **Step 6: Bootstrap und `UserAdminService` implementieren**

`Users/AdminBootstrapper.cs`:

```csharp
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
            await roles.CreateAsync(new IdentityRole(Roles.Admin));
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

        await users.AddToRoleAsync(admin, Roles.Admin);
    }
}
```

`Users/UserAdminService.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Webionic.ICalMerger.Calendars;
using Webionic.ICalMerger.Data;

namespace Webionic.ICalMerger.Users;

public sealed record UserRow(string Id, string Email, bool IsAdmin, bool IsLockedOut, bool HasPassword);

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

        var rows = new List<UserRow>();
        foreach (var user in all)
        {
            rows.Add(new UserRow(
                user.Id,
                user.Email ?? user.UserName ?? "",
                adminIds.Contains(user.Id),
                await users.IsLockedOutAsync(user),
                user.PasswordHash is not null));
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
```

- [ ] **Step 7: Tests laufen lassen**

Run: `dotnet test tests/Webionic.ICalMerger.Tests`
Expected: alle Tests PASS (auch die der früheren Tasks). Mögliche Stolpersteine, die du am Code und nicht am Test behebst:
- `Login_*`-Tests ohne Antiforgery-Token: Die Regex in `WebHelpers` passt nicht zur HTML-Ausgabe (Attribut-Reihenfolge). Passe die Regex an die echte Ausgabe an.
- `SelfServicePages_DoNotExist` liefert `200` statt `404`: Es sind noch Vorlagenseiten übrig, lösche sie.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: add user administration, admin bootstrap and account flow tests" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Kalender-UI (Übersicht und Detailseite)

**Files:**
- Create: `src/Webionic.ICalMerger/Users/CurrentUser.cs`
- Modify: `src/Webionic.ICalMerger/ServiceRegistration.cs`, `src/Webionic.ICalMerger/Components/_Imports.razor`, `src/Webionic.ICalMerger/Components/Pages/Home.razor`
- Create: `src/Webionic.ICalMerger/Components/Pages/CalendarDetail.razor`
- Test: Build + manueller Test (Blazor-Komponententests sind bewusst nicht Teil des Umfangs; die Logik steckt in getesteten Services)

**Interfaces:**
- Consumes: `CalendarService` (alle Methoden aus Task 4), `DomainException`, `MergedCalendar`, `CalendarSource`.
- Produces: `sealed class CurrentUser(AuthenticationStateProvider auth)` mit `Task<string> GetIdAsync()` (scoped). Seiten `/` und `/calendars/{Id:int}`.

- [ ] **Step 1: `CurrentUser`**

`Users/CurrentUser.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Webionic.ICalMerger.Users;

public sealed class CurrentUser(AuthenticationStateProvider authentication)
{
    public async Task<string> GetIdAsync()
    {
        var state = await authentication.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? throw new InvalidOperationException("Nicht angemeldet.");
    }
}
```

In `ServiceRegistration.AddCalendarServices` vor `return services;` ergänzen: `services.AddScoped<Users.CurrentUser>();`.

In `Components/_Imports.razor` ergänzen:

```razor
@using Webionic.ICalMerger.Calendars
@using Webionic.ICalMerger.Data
@using Webionic.ICalMerger.Users
```

- [ ] **Step 2: Übersichtsseite**

`Components/Pages/Home.razor` ersetzen:

```razor
@page "/"
@attribute [Authorize]
@rendermode @(new InteractiveServerRenderMode(prerender: false))

@inject CalendarService Calendars
@inject CurrentUser User

<PageTitle>Meine Kalender</PageTitle>

<h1>Meine Kalender</h1>

@if (error is not null)
{
    <div class="alert alert-danger" role="alert">@error</div>
}

@if (calendars is null)
{
    <p>Lade …</p>
}
else
{
    @if (calendars.Count == 0)
    {
        <p class="text-muted">Du hast noch keinen Kalender. Lege unten deinen ersten an.</p>
    }
    else
    {
        <div class="list-group mb-4">
            @foreach (var calendar in calendars)
            {
                <a class="list-group-item list-group-item-action d-flex justify-content-between align-items-center"
                   href="calendars/@calendar.Id">
                    <span>@calendar.Name</span>
                    <span class="d-flex gap-2 align-items-center">
                        @if (calendar.Sources.Any(s => s.LastError is not null))
                        {
                            <span class="badge text-bg-warning">Fehler bei einer Quelle</span>
                        }
                        <span class="badge text-bg-secondary">@calendar.Sources.Count @(calendar.Sources.Count == 1 ? "Quelle" : "Quellen")</span>
                    </span>
                </a>
            }
        </div>
    }

    <EditForm Model="form" OnValidSubmit="CreateAsync" FormName="create-calendar" class="row g-2">
        <div class="col-auto">
            <label class="visually-hidden" for="new-name">Name des neuen Kalenders</label>
            <InputText id="new-name" @bind-Value="form.Name" class="form-control" placeholder="Name, z. B. Familie" maxlength="100" />
        </div>
        <div class="col-auto">
            <button type="submit" class="btn btn-primary" disabled="@busy">Kalender anlegen</button>
        </div>
    </EditForm>
}

@code {
    private List<MergedCalendar>? calendars;
    private string ownerId = "";
    private string? error;
    private bool busy;
    private readonly NameForm form = new();

    protected override async Task OnInitializedAsync()
    {
        ownerId = await User.GetIdAsync();
        calendars = await Calendars.ListAsync(ownerId);
    }

    private async Task CreateAsync()
    {
        error = null;
        busy = true;
        try
        {
            await Calendars.CreateAsync(ownerId, form.Name);
            form.Name = "";
            calendars = await Calendars.ListAsync(ownerId);
        }
        catch (DomainException ex)
        {
            error = ex.Message;
        }
        finally
        {
            busy = false;
        }
    }

    private sealed class NameForm
    {
        public string Name { get; set; } = "";
    }
}
```

- [ ] **Step 3: Detailseite**

`Components/Pages/CalendarDetail.razor`:

```razor
@page "/calendars/{Id:int}"
@attribute [Authorize]
@rendermode @(new InteractiveServerRenderMode(prerender: false))

@inject CalendarService Calendars
@inject CurrentUser User
@inject NavigationManager Navigation
@inject IJSRuntime Js

<PageTitle>@(calendar?.Name ?? "Kalender")</PageTitle>

@if (loading)
{
    <p>Lade …</p>
}
else if (calendar is null)
{
    <div class="alert alert-warning">Kalender nicht gefunden. <a href="">Zur Übersicht</a></div>
}
else
{
    <p><a href="">← Meine Kalender</a></p>
    <h1>@calendar.Name</h1>

    @if (error is not null)
    {
        <div class="alert alert-danger" role="alert">@error</div>
    }
    @if (info is not null)
    {
        <div class="alert alert-success" role="status">@info</div>
    }

    <section class="mb-4">
        <h2 class="h5">Feed-URL</h2>
        <p class="text-muted">
            Diese URL kannst du in Apple Kalender, Google Kalender oder Outlook abonnieren und an deine Familie weitergeben.
            Wer die URL kennt, kann den Kalender lesen.
        </p>
        <div class="input-group mb-2">
            <input class="form-control" readonly value="@FeedUrl" aria-label="Feed-URL" />
            <button class="btn btn-outline-secondary" @onclick="CopyAsync">Kopieren</button>
        </div>
        @if (confirmRegenerate)
        {
            <div class="alert alert-warning">
                Die bisherige URL wird sofort ungültig. Alle, die sie abonniert haben, brauchen die neue URL.
                <div class="mt-2 d-flex gap-2">
                    <button class="btn btn-warning btn-sm" @onclick="RegenerateAsync" disabled="@busy">Ja, neue URL erzeugen</button>
                    <button class="btn btn-outline-secondary btn-sm" @onclick="() => confirmRegenerate = false">Abbrechen</button>
                </div>
            </div>
        }
        else
        {
            <button class="btn btn-link p-0" @onclick="() => confirmRegenerate = true">Neue URL erzeugen</button>
        }
    </section>

    <section class="mb-4">
        <h2 class="h5">Quellen</h2>
        <p class="text-muted">Bei doppelten Terminen gewinnt die Quelle weiter oben.</p>

        @if (calendar.Sources.Count == 0)
        {
            <p class="text-muted">Noch keine Quellen.</p>
        }
        else
        {
            <ul class="list-group mb-3">
                @foreach (var source in calendar.Sources)
                {
                    <li class="list-group-item">
                        @if (editingId == source.Id)
                        {
                            <div class="row g-2">
                                <div class="col-md-3">
                                    <input class="form-control" @bind="edit.Name" @bind:event="oninput" placeholder="Name" maxlength="100" aria-label="Name der Quelle" />
                                </div>
                                <div class="col-md-6">
                                    <input class="form-control" @bind="edit.Url" @bind:event="oninput" placeholder="https://… oder webcal://…" aria-label="URL der Quelle" />
                                </div>
                                <div class="col-md-3 d-flex gap-2">
                                    <button class="btn btn-primary btn-sm" @onclick="() => SaveEditAsync(source.Id)" disabled="@busy">Speichern</button>
                                    <button class="btn btn-outline-secondary btn-sm" @onclick="() => editingId = null">Abbrechen</button>
                                </div>
                            </div>
                        }
                        else
                        {
                            <div class="d-flex justify-content-between align-items-start gap-2">
                                <div>
                                    <strong>@source.Name</strong>
                                    <div class="text-muted small">@Mask(source.Url)</div>
                                    @if (source.LastError is not null)
                                    {
                                        <div class="text-danger small">Fehler: @source.LastError@(source.LastSuccessAt is null ? "" : $" (zuletzt erfolgreich: {Format(source.LastSuccessAt)})")</div>
                                    }
                                    else if (source.LastSuccessAt is not null)
                                    {
                                        <div class="text-success small">Zuletzt erfolgreich: @Format(source.LastSuccessAt)</div>
                                    }
                                    else
                                    {
                                        <div class="text-muted small">Noch nicht abgerufen</div>
                                    }
                                </div>
                                <div class="btn-group btn-group-sm" role="group" aria-label="Aktionen für @source.Name">
                                    <button class="btn btn-outline-secondary" title="Nach oben" aria-label="Nach oben" @onclick="() => MoveAsync(source.Id, -1)">↑</button>
                                    <button class="btn btn-outline-secondary" title="Nach unten" aria-label="Nach unten" @onclick="() => MoveAsync(source.Id, 1)">↓</button>
                                    <button class="btn btn-outline-secondary" @onclick="() => StartEdit(source)">Bearbeiten</button>
                                    <button class="btn btn-outline-danger" @onclick="() => DeleteSourceAsync(source.Id)">Entfernen</button>
                                </div>
                            </div>
                        }
                    </li>
                }
            </ul>
        }

        <h3 class="h6">Quelle hinzufügen</h3>
        <div class="row g-2">
            <div class="col-md-3">
                <input class="form-control" @bind="newSource.Name" @bind:event="oninput" placeholder="Name (optional)" maxlength="100" aria-label="Name der neuen Quelle" />
            </div>
            <div class="col-md-6">
                <input class="form-control" @bind="newSource.Url" @bind:event="oninput" placeholder="https://… oder webcal://…" aria-label="URL der neuen Quelle" />
            </div>
            <div class="col-md-3">
                <button class="btn btn-primary" @onclick="AddSourceAsync" disabled="@busy">@(busy ? "Prüfe URL …" : "Hinzufügen")</button>
            </div>
        </div>
    </section>

    <section class="mb-4">
        <h2 class="h5">Kalender verwalten</h2>
        <div class="row g-2 mb-3">
            <div class="col-md-6">
                <input class="form-control" @bind="renameValue" @bind:event="oninput" maxlength="100" aria-label="Name des Kalenders" />
            </div>
            <div class="col-md-3">
                <button class="btn btn-outline-primary" @onclick="RenameAsync" disabled="@busy">Umbenennen</button>
            </div>
        </div>
        @if (confirmDelete)
        {
            <div class="alert alert-danger">
                Kalender „@calendar.Name“ samt Feed-URL wirklich löschen?
                <div class="mt-2 d-flex gap-2">
                    <button class="btn btn-danger btn-sm" @onclick="DeleteCalendarAsync" disabled="@busy">Ja, löschen</button>
                    <button class="btn btn-outline-secondary btn-sm" @onclick="() => confirmDelete = false">Abbrechen</button>
                </div>
            </div>
        }
        else
        {
            <button class="btn btn-outline-danger" @onclick="() => confirmDelete = true">Kalender löschen</button>
        }
    </section>
}

@code {
    [Parameter]
    public int Id { get; set; }

    private MergedCalendar? calendar;
    private bool loading = true;
    private bool busy;
    private string ownerId = "";
    private string? error;
    private string? info;
    private string renameValue = "";
    private bool confirmRegenerate;
    private bool confirmDelete;
    private int? editingId;
    private SourceForm newSource = new();
    private SourceForm edit = new();

    private string FeedUrl => Navigation.ToAbsoluteUri($"feed/{calendar!.Token}.ics").ToString();

    protected override async Task OnInitializedAsync()
    {
        ownerId = await User.GetIdAsync();
        await LoadAsync();
        loading = false;
    }

    private async Task LoadAsync()
    {
        calendar = await Calendars.GetAsync(ownerId, Id);
        renameValue = calendar?.Name ?? "";
    }

    /// <summary>Führt eine Aktion aus, zeigt DomainException-Meldungen an und lädt den Kalender neu.</summary>
    private async Task RunAsync(Func<Task> action, string? success = null)
    {
        error = null;
        info = null;
        busy = true;
        try
        {
            await action();
            await LoadAsync();
            info = success;
        }
        catch (DomainException ex)
        {
            error = ex.Message;
        }
        finally
        {
            busy = false;
        }
    }

    private Task AddSourceAsync() => RunAsync(async () =>
    {
        await Calendars.AddSourceAsync(ownerId, Id, newSource.Name, newSource.Url);
        newSource = new();
    }, "Quelle hinzugefügt.");

    private void StartEdit(CalendarSource source)
    {
        editingId = source.Id;
        edit = new() { Name = source.Name, Url = source.Url };
    }

    private Task SaveEditAsync(int sourceId) => RunAsync(async () =>
    {
        await Calendars.UpdateSourceAsync(ownerId, sourceId, edit.Name, edit.Url);
        editingId = null;
    }, "Quelle gespeichert.");

    private Task DeleteSourceAsync(int sourceId) => RunAsync(() => Calendars.DeleteSourceAsync(ownerId, sourceId), "Quelle entfernt.");

    private Task MoveAsync(int sourceId, int direction) => RunAsync(() => Calendars.MoveSourceAsync(ownerId, sourceId, direction));

    private Task RenameAsync() => RunAsync(() => Calendars.RenameAsync(ownerId, Id, renameValue), "Name gespeichert.");

    private Task RegenerateAsync() => RunAsync(async () =>
    {
        await Calendars.RegenerateTokenAsync(ownerId, Id);
        confirmRegenerate = false;
    }, "Neue Feed-URL erzeugt. Die alte URL ist nicht mehr gültig.");

    private async Task DeleteCalendarAsync()
    {
        error = null;
        busy = true;
        try
        {
            await Calendars.DeleteAsync(ownerId, Id);
            Navigation.NavigateTo("");
        }
        catch (DomainException ex)
        {
            error = ex.Message;
            busy = false;
        }
    }

    private async Task CopyAsync()
    {
        try
        {
            await Js.InvokeVoidAsync("navigator.clipboard.writeText", FeedUrl);
            info = "URL kopiert.";
        }
        catch (JSException)
        {
            error = "Kopieren ist hier nicht möglich. Bitte die URL von Hand markieren.";
        }
    }

    private static string Mask(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "…";
        var path = uri.AbsolutePath;
        return $"{uri.Host}{(path.Length > 12 ? path[..12] : path)}…";
    }

    private static string Format(DateTime? utc) =>
        utc is null ? "" : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToString("dd.MM.yyyy HH:mm") + " UTC";

    private sealed class SourceForm
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
    }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: Build succeeded. Behebe Razor-Fehler in der Komponente (z. B. Lambda-Syntax in `@onclick`), nicht durch Entfernen von Funktionen.

- [ ] **Step 5: Gesamtsuite**

Run: `dotnet test`
Expected: alle PASS. `Login_WithBootstrapAdmin_SetsCookieAndOpensHome` ruft `/` auf und muss weiterhin `200` liefern.

- [ ] **Step 6: Manueller Test im Browser**

```bash
ADMIN_EMAIL=admin@example.com ADMIN_PASSWORD='correct horse battery' dotnet run --project src/Webionic.ICalMerger --urls http://localhost:5099
```

Prüfe im Browser unter `http://localhost:5099` (zum Beispiel mit den Chrome-Tools von Claude oder von Hand):
1. Anmelden mit den obigen Daten, danach Weiterleitung auf „Meine Kalender“.
2. Kalender „Test“ anlegen und öffnen.
3. Quelle hinzufügen: `webcal://calendar.google.com/calendar/ical/de.austrian%23holiday%40group.v.calendar.google.com/public/basic.ics`. Sie wird akzeptiert und zeigt „Zuletzt erfolgreich“ erst nach dem ersten Feed-Abruf.
4. Feed-URL mit `curl -s <URL> | head -20`: Der Abruf liefert `BEGIN:VCALENDAR` und Termine, und die Quelle zeigt danach „Zuletzt erfolgreich“.
5. Quelle mit `http://127.0.0.1:8000/x.ics` hinzufügen: Sie wird mit „Adresse nicht erlaubt …“ abgelehnt.
6. Neue URL erzeugen: Die alte URL liefert `404`.
7. Seite mit schmalem Fenster (Handybreite) ansehen: kein horizontales Scrollen.

Beende den Server danach (Strg+C).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add calendar overview and detail pages" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Admin-Seite für die Nutzerverwaltung

**Files:**
- Create: `src/Webionic.ICalMerger/Components/Pages/AdminUsers.razor`
- Modify: `src/Webionic.ICalMerger/Components/Routes.razor`
- Test: `tests/Webionic.ICalMerger.Tests/Users/AdminPageAccessTests.cs`

**Interfaces:**
- Consumes: `UserAdminService` (Task 7), `CurrentUser` (Task 8), `Roles.Admin`, `WebHelpers`, `AppFactory`.
- Produces: Seite `/admin/users` (nur Rolle `Admin`).

- [ ] **Step 1: Failing Tests**

`tests/Webionic.ICalMerger.Tests/Users/AdminPageAccessTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Webionic.ICalMerger.Tests.Support;
using Webionic.ICalMerger.Users;

namespace Webionic.ICalMerger.Tests.Users;

public sealed class AdminPageAccessTests : IDisposable
{
    private readonly AppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task AdminPage_WithoutLogin_RedirectsToLogin()
    {
        var response = await _factory.NewClient().GetAsync("/admin/users");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task AdminPage_ForAdmin_Opens()
    {
        var client = _factory.NewClient();
        await WebHelpers.LoginAsync(client, AppFactory.AdminEmail, AppFactory.AdminPassword);

        var response = await client.GetAsync("/admin/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AdminPage_ForNormalUser_ShowsNoAccess()
    {
        var admin = _factory.Services.GetRequiredService<UserAdminService>();
        var link = await admin.InviteAsync("user@example.com", "http://localhost/");
        var pathAndQuery = new Uri(link).PathAndQuery;
        var code = pathAndQuery[(pathAndQuery.IndexOf("code=", StringComparison.Ordinal) + 5)..];
        var token = System.Text.Encoding.UTF8.GetString(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(code));
        var setup = _factory.NewClient();
        await WebHelpers.PostFormAsync(setup, pathAndQuery, "reset-password", new Dictionary<string, string>
        {
            ["Input.Email"] = "user@example.com",
            ["Input.Password"] = "ein langes passwort",
            ["Input.ConfirmPassword"] = "ein langes passwort",
            ["Input.Code"] = token,
        });

        var client = _factory.NewClient();
        await WebHelpers.LoginAsync(client, "user@example.com", "ein langes passwort");
        var response = await client.GetAsync("/admin/users");

        Assert.Contains("Kein Zugriff", await response.Content.ReadAsStringAsync());
    }
}
```

- [ ] **Step 2: Tests laufen lassen, Fehlschlag prüfen**

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~AdminPageAccessTests"`
Expected: `AdminPage_ForAdmin_Opens` FAIL (404), `AdminPage_ForNormalUser_ShowsNoAccess` FAIL, `AdminPage_WithoutLogin_RedirectsToLogin` FAIL (404 statt 302).

- [ ] **Step 3: `Routes.razor` anpassen**

Ersetze den Inhalt durch:

```razor
@using Webionic.ICalMerger.Components.Account.Shared
<Router AppAssembly="typeof(Program).Assembly" NotFoundPage="typeof(Pages.NotFound)">
    <Found Context="routeData">
        <AuthorizeRouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)">
            <NotAuthorized>
                @if (context.User.Identity?.IsAuthenticated == true)
                {
                    <h1>Kein Zugriff</h1>
                    <p class="text-danger">Du hast keine Berechtigung für diese Seite.</p>
                }
                else
                {
                    <RedirectToLogin />
                }
            </NotAuthorized>
        </AuthorizeRouteView>
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
</Router>
```

- [ ] **Step 4: Admin-Seite**

`Components/Pages/AdminUsers.razor`:

```razor
@page "/admin/users"
@attribute [Authorize(Roles = Roles.Admin)]
@rendermode @(new InteractiveServerRenderMode(prerender: false))

@inject UserAdminService Admin
@inject CurrentUser User
@inject NavigationManager Navigation
@inject IJSRuntime Js

<PageTitle>Nutzer</PageTitle>

<h1>Nutzer</h1>

@if (error is not null)
{
    <div class="alert alert-danger" role="alert">@error</div>
}
@if (info is not null)
{
    <div class="alert alert-success" role="status">@info</div>
}

@if (link is not null)
{
    <div class="alert alert-info">
        <strong>@linkTitle</strong>
        <p class="mb-2">Dieser Link wird nur jetzt angezeigt. Schicke ihn selbst weiter (zum Beispiel per Messenger). Er ist 7 Tage gültig und funktioniert nur einmal.</p>
        <div class="input-group">
            <input class="form-control" readonly value="@link" aria-label="Einladungslink" />
            <button class="btn btn-outline-secondary" @onclick="CopyLinkAsync">Kopieren</button>
        </div>
    </div>
}

<section class="mb-4">
    <h2 class="h5">Nutzer einladen</h2>
    <div class="row g-2">
        <div class="col-md-6">
            <input class="form-control" type="email" @bind="inviteEmail" @bind:event="oninput" placeholder="name@example.com" aria-label="E-Mail-Adresse des neuen Nutzers" />
        </div>
        <div class="col-md-3">
            <button class="btn btn-primary" @onclick="InviteAsync" disabled="@busy">Einladen</button>
        </div>
    </div>
</section>

@if (users is null)
{
    <p>Lade …</p>
}
else
{
    <div class="table-responsive">
        <table class="table align-middle">
            <thead>
                <tr><th>E-Mail</th><th>Status</th><th class="text-end">Aktionen</th></tr>
            </thead>
            <tbody>
                @foreach (var user in users)
                {
                    <tr>
                        <td>
                            @user.Email
                            @if (user.Id == myId) { <span class="text-muted">(du)</span> }
                        </td>
                        <td>
                            @if (user.IsAdmin) { <span class="badge text-bg-primary">Admin</span> }
                            @if (user.IsLockedOut) { <span class="badge text-bg-danger">gesperrt</span> }
                            @if (!user.HasPassword) { <span class="badge text-bg-warning">Passwort fehlt noch</span> }
                        </td>
                        <td class="text-end">
                            @if (confirmDeleteId == user.Id)
                            {
                                <span class="me-2">Wirklich löschen? Alle Kalender dieses Nutzers werden mitgelöscht.</span>
                                <button class="btn btn-danger btn-sm" @onclick="() => DeleteAsync(user.Id)" disabled="@busy">Ja, löschen</button>
                                <button class="btn btn-outline-secondary btn-sm" @onclick="() => confirmDeleteId = null">Abbrechen</button>
                            }
                            else
                            {
                                <div class="btn-group btn-group-sm" role="group" aria-label="Aktionen für @user.Email">
                                    <button class="btn btn-outline-secondary" @onclick="() => ResetLinkAsync(user)">@(user.HasPassword ? "Passwort-Reset-Link" : "Neuer Einladungslink")</button>
                                    <button class="btn btn-outline-secondary" @onclick="() => SetLockedAsync(user.Id, !user.IsLockedOut)">@(user.IsLockedOut ? "Entsperren" : "Sperren")</button>
                                    <button class="btn btn-outline-secondary" @onclick="() => SetAdminAsync(user.Id, !user.IsAdmin)">@(user.IsAdmin ? "Admin entziehen" : "Zum Admin machen")</button>
                                    <button class="btn btn-outline-danger" @onclick="() => confirmDeleteId = user.Id" disabled="@(user.Id == myId)">Löschen</button>
                                </div>
                            }
                        </td>
                    </tr>
                }
            </tbody>
        </table>
    </div>
}

@code {
    private List<UserRow>? users;
    private string myId = "";
    private string inviteEmail = "";
    private string? confirmDeleteId;
    private string? link;
    private string linkTitle = "";
    private string? error;
    private string? info;
    private bool busy;

    private string BaseUri => Navigation.BaseUri;

    protected override async Task OnInitializedAsync()
    {
        myId = await User.GetIdAsync();
        users = await Admin.ListAsync();
    }

    private async Task RunAsync(Func<Task> action, string? success = null)
    {
        error = null;
        info = null;
        busy = true;
        try
        {
            await action();
            users = await Admin.ListAsync();
            info = success;
        }
        catch (DomainException ex)
        {
            error = ex.Message;
        }
        finally
        {
            busy = false;
        }
    }

    private Task InviteAsync() => RunAsync(async () =>
    {
        link = await Admin.InviteAsync(inviteEmail, BaseUri);
        linkTitle = $"Einladungslink für {inviteEmail.Trim()}";
        inviteEmail = "";
    }, "Nutzer angelegt.");

    private Task ResetLinkAsync(UserRow user) => RunAsync(async () =>
    {
        link = await Admin.CreateResetLinkAsync(user.Id, BaseUri);
        linkTitle = $"Link für {user.Email}";
    });

    private Task SetLockedAsync(string id, bool locked) =>
        RunAsync(() => Admin.SetLockedAsync(id, locked), locked ? "Nutzer gesperrt." : "Nutzer entsperrt.");

    private Task SetAdminAsync(string id, bool isAdmin) =>
        RunAsync(() => Admin.SetAdminAsync(id, isAdmin), isAdmin ? "Nutzer ist jetzt Admin." : "Admin-Rolle entzogen.");

    private Task DeleteAsync(string id) => RunAsync(async () =>
    {
        await Admin.DeleteAsync(id, myId);
        confirmDeleteId = null;
    }, "Nutzer gelöscht.");

    private async Task CopyLinkAsync()
    {
        try
        {
            await Js.InvokeVoidAsync("navigator.clipboard.writeText", link);
            info = "Link kopiert.";
        }
        catch (JSException)
        {
            error = "Kopieren ist hier nicht möglich. Bitte den Link von Hand markieren.";
        }
    }
}
```

- [ ] **Step 5: Tests laufen lassen**

Run: `dotnet test`
Expected: alle PASS. Liefert `AdminPage_ForNormalUser_ShowsNoAccess` weder „Kein Zugriff“ noch einen Redirect, prüfe, ob `AuthorizeRouteView` die Rollenprüfung für `[Authorize(Roles = ...)]` auf Routen mit `@rendermode` anwendet. Fehlt sie, ergänze zusätzlich die Seite um `<AuthorizeView Roles="Admin">` und lass die Tests unverändert.

- [ ] **Step 6: Manueller Test**

Starte die App wie in Task 8 und prüfe als Admin:
1. Nutzer „familie@example.com“ einladen. Der Link erscheint, daneben das Badge „Passwort fehlt noch“.
2. Link in einem privaten Fenster öffnen, E-Mail und Passwort setzen, danach anmelden und einen eigenen Kalender anlegen.
3. Als Admin sperren (Nutzer wird nach spätestens 1 Minute ausgeloggt und kann sich nicht mehr anmelden), wieder entsperren und löschen.
4. Den einzigen Admin zu löschen oder herabzustufen scheitert mit der deutschen Fehlermeldung.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add admin user management page" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Docker, Dokploy-Doku, Restübersetzungen und Gesamtprüfung

**Files:**
- Create: `Dockerfile`, `.dockerignore`
- Modify: `README.md`, `src/Webionic.ICalMerger/Components/Pages/Error.razor`, `src/Webionic.ICalMerger/Components/Pages/NotFound.razor`, `src/Webionic.ICalMerger/Components/Layout/ReconnectModal.razor`
- Test: Docker-Build und -Lauf (wenn Docker verfügbar), Gesamtsuite

**Interfaces:**
- Consumes: alles Bisherige.
- Produces: lauffähiges Image mit `ASPNETCORE_URLS=http://+:8080`, Volume `/data`.

- [ ] **Step 1: Restliche englische Texte übersetzen**

Lies `Components/Pages/NotFound.razor`, `Components/Pages/Error.razor` und `Components/Layout/ReconnectModal.razor` und übersetze alle sichtbaren Texte ins Deutsche (z. B. „Seite nicht gefunden“ / „Entschuldigung, diese Seite gibt es nicht.“, „Es ist ein Fehler aufgetreten.“, „Verbindung zum Server wird wiederhergestellt …“, „Erneut versuchen“). Ändere nichts an Markup, Klassen oder Skripten. Prüfe mit `grep -rn "Error\|Rejoin\|Retry\|Not Found" src/Webionic.ICalMerger/Components --include=*.razor`, dass keine englischen Resttexte mehr sichtbar sind.

- [ ] **Step 2: `.dockerignore`**

```
**/bin
**/obj
**/data
.git
.claude
docs
tests
*.md
```

- [ ] **Step 3: `Dockerfile`**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Webionic.ICalMerger/Webionic.ICalMerger.csproj src/Webionic.ICalMerger/
RUN dotnet restore src/Webionic.ICalMerger
COPY src ./src
RUN dotnet publish src/Webionic.ICalMerger -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__DefaultConnection="Data Source=/data/app.db;Default Timeout=30" \
    DataProtection__KeysPath=/data/keys

# Daten (SQLite und Data-Protection-Schlüssel) liegen auf dem Volume /data.
RUN mkdir -p /data && chown -R $APP_UID /data
USER $APP_UID
VOLUME /data
EXPOSE 8080

ENTRYPOINT ["dotnet", "Webionic.ICalMerger.dll"]
```

- [ ] **Step 4: `README.md`**

```markdown
# iCal Merger

Fasst mehrere iCal-URLs zu einer Feed-URL zusammen, zum Beispiel für den Familienkalender.
Mehrere Nutzer, jeder mit eigenen Kalendern. Neue Nutzer lädt der Admin per Link ein (kein E-Mail-Versand nötig).

## So funktioniert es

1. Anmelden und einen Kalender anlegen.
2. iCal-URLs (`https://…` oder `webcal://…`) als Quellen hinzufügen.
3. Die angezeigte Feed-URL (`https://<host>/feed/<token>.ics`) in Apple Kalender, Google Kalender oder Outlook abonnieren und an die Familie weitergeben.

Wer die Feed-URL kennt, kann den Kalender lesen. Mit „Neue URL erzeugen“ wird die alte URL ungültig.
Quellen werden live abgerufen (5 Minuten Cache). Fällt eine Quelle aus, liefert der Feed die übrigen Quellen
und zeigt bei Bedarf den letzten bekannten Stand der ausgefallenen.

Hinweis: Quell-URLs, die auf interne Adressen zeigen (localhost, 10.x, 192.168.x, 172.16–31.x, Link-Local),
werden aus Sicherheitsgründen abgelehnt.

## Betrieb auf Dokploy

1. Neue Anwendung aus diesem Repository anlegen, Build-Typ **Dockerfile**, Port **8080**.
2. Umgebungsvariablen setzen:
   - `ADMIN_EMAIL` und `ADMIN_PASSWORD`: legen beim ersten Start den ersten Admin an (Passwort mindestens 10 Zeichen). Danach werden sie nicht mehr gebraucht.
3. Volume auf `/data` mounten (enthält die SQLite-Datenbank und die Schlüssel für Logins).
4. Domain mit HTTPS zuweisen. Die App wertet die `X-Forwarded-*`-Header des Proxys aus.

Optionale Einstellungen:

| Variable | Standard | Bedeutung |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | `Data Source=/data/app.db;Default Timeout=30` | Datenbank |
| `DataProtection__KeysPath` | `/data/keys` | Ablage der Schlüssel |
| `Limits__MaxCalendarsPerUser` | `10` | Kalender pro Nutzer |
| `Limits__MaxSourcesPerCalendar` | `20` | Quellen pro Kalender |

## Nutzerverwaltung

Als Admin unter **Nutzer**: Nutzer einladen, Einladungs- oder Passwort-Reset-Link erzeugen, sperren,
zum Admin machen, löschen. Der Link wird einmal angezeigt, ist 7 Tage gültig und wird von dir selbst weitergegeben.

## Entwicklung

```bash
dotnet test
ADMIN_EMAIL=admin@example.com ADMIN_PASSWORD='correct horse battery' dotnet run --project src/Webionic.ICalMerger
```

Die Daten liegen lokal in `src/Webionic.ICalMerger/data/`.
```

- [ ] **Step 5: Gesamtprüfung**

```bash
dotnet build -c Release
dotnet test
```

Expected: Build succeeded, alle Tests PASS.

Wenn Docker installiert ist (`command -v docker`):

```bash
docker build -t icalmerger .
docker run -d --name icalmerger-smoke -p 8089:8080 -e ADMIN_EMAIL=admin@example.com -e ADMIN_PASSWORD='correct horse battery' -v icalmerger-smoke-data:/data icalmerger
sleep 8
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:8089/Account/Login
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:8089/feed/doesnotexistdoesnotexist.ics
docker rm -f icalmerger-smoke && docker volume rm icalmerger-smoke-data
```

Expected: `200` und `404`. Schlägt der Start fehl (`docker logs icalmerger-smoke`), behebe die Ursache (häufig Schreibrechte auf `/data`).

Ist Docker nicht installiert, sag das ausdrücklich im Abschlussbericht und verlasse dich nicht auf das Dockerfile als „geprüft“.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add Dockerfile, README and German texts for remaining pages" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

## Self-Review (vom Plan-Autor ausgeführt)

**Spec-Abdeckung:**
- Blazor Server, .NET 10, SQLite, Identity mit Rollen, Docker/Dokploy: Task 1, 10.
- Merge-Regeln (nur VEVENT/VTIMEZONE, Dedupe, TZID einmal, Faltung, Header): Task 2.
- Abruf (Timeout, 10 MB, 5 Redirects, SSRF per ConnectCallback, `webcal://`): Task 3.
- Cache 5 Min, Stale-on-Error, Single-Flight, Status je Quelle: Task 5 (plus Fehler-Backoff als Ergänzung gegen Dauerabrufe).
- Feed-Endpunkt mit 404/503/200 und Headern, Token neu erzeugen: Task 4 (Token), Task 6.
- Limits, Besitzer-Filter, URL-Probeabruf beim Hinzufügen: Task 4.
- Nutzerverwaltung (Einladungslink, Reset, Sperren, Admin-Rolle, Löschen, letzter Admin, Selbstlöschen, Bootstrap per Env): Task 7, 9.
- UI auf Deutsch, URLs gekürzt, Kopieren-Button, Bestätigungen: Task 1, 8, 9, 10.
- Forwarded Headers, Volume `/data`, Data-Protection-Keys, Auto-Migration: Task 1, 10.

**Abweichungen von der Spec (bewusst, im Plan festgehalten):**
- Der Connection-String heißt `DefaultConnection` (Vorlagenname) statt `Default`.
- Der Link nutzt die Vorlagenseite `/Account/ResetPassword?code=…` statt einer neuen Seite `/Account/SetPassword?userId=…&token=…`. Die Seite fragt zur Bestätigung die E-Mail-Adresse ab.
- Zusätzlich: Fehler-Backoff von 1 Minute pro Quelle und Sperrung nach 5 Fehlversuchen beim Login (Identity-Lockout), damit weder ausgefallene Quellen noch Passwort-Raten den Dienst belasten.

**Typkonsistenz:** `CalendarService`-Signaturen (Task 4) stimmen mit den Aufrufen in Task 6 (`RegenerateTokenAsync`), Task 8 (UI) und Tests überein. `FeedService.BuildAsync` → `FeedResult?` (Task 5) wird in Task 6 so verwendet. `UserAdminService`-Signaturen (Task 7) stimmen mit Task 9 überein. `WebHelpers`/`AppFactory` aus Task 6 und 7 werden in Task 9 wiederverwendet.

**Review Focus:** Jede der fünf Zeilen hat Tests: BOM/LF/ATTACH und abgeschnittene Termine sowie Namens-Escaping (Task 2), HTML-Antwort (Task 4 und Task 5), Müll-Token (Task 6).
