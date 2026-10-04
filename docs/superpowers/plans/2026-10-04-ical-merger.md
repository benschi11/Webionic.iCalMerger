# iCal Merger Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Blazor-Server-App, in der angemeldete Nutzer mehrere iCal-URLs zu Merge-Kalendern mit je eigener, token-geschützter Feed-URL zusammenfassen. Nutzerverwaltung über ASP.NET Core Identity, Betrieb als Docker-Container auf Dokploy.

**Architecture:** Eine ASP.NET-Core-App (.NET 10) mit Blazor Interactive Server, EF Core/SQLite und Identity. Der Merge ist eine reine, textbasierte Funktion (`IcsMerger`). Quellen werden über einen SSRF-sicheren `HttpClient` mit Cache (`SourceCache`) geholt. Der Feed ist ein anonymer Minimal-API-Endpunkt `GET /feed/{token}.ics`.

**Tech Stack:** .NET 10, Blazor Web App (Interactive Server), EF Core 10 + SQLite, ASP.NET Core Identity (mit Rollen), Tailwind CSS v4 (Standalone-CLI, kein Node, kein Bootstrap), xUnit, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.TimeProvider.Testing`, Docker.

**Spec:** `docs/superpowers/specs/2026-10-04-ical-merger-design.md`

**UI-Referenz:** `docs/mockups/index.html` (vom Nutzer abgenommene Mockups: Kalenderübersicht, Kalenderdetail, Nutzerverwaltung, jeweils mit Handyansicht und Bestätigungsdialog). Produktkontext und Prinzipien: `PRODUCT.md`. Markenwerte: `/home/benedikt/_DEV/Webionic.Website/DESIGN.md`.

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
- UI im Webionic-Theme (Task 7b): Navy und Honig-Amber, Bricolage Grotesque (Überschriften) und Hanken Grotesk (Text), selbst gehostet; Sechseck als Statuszeichen; Amber nur für die eine Hauptaktion pro Ansicht; Fehlerrot nur für Fehlerzustände. Quell-Status in drei Stufen plus „unbekannt“: Ok (Navy), Zwischenspeicher/`Stale` (Amber), Fehler (Rot), Unbekannt (Grau). Gefährliche Aktionen (neue Feed-Adresse, Kalender löschen, Nutzer löschen) laufen über einen Bestätigungsdialog mit Folgetext.
- CSS: Tailwind CSS v4 über die Standalone-CLI (Version in der `.csproj` gepinnt, wird beim ersten Build nach `tools/tailwind/` geladen). Eingabe `src/Webionic.ICalMerger/Styles/app.css`, Ausgabe `wwwroot/css/app.css`. Die Ausgabe wird bei jedem Build neu erzeugt und mit eingecheckt, weil eine erst während des Builds entstehende Datei in `wwwroot` sonst im allerersten Build nicht in den Static-Asset-Manifest gelangen kann. Kein Bootstrap, kein Node. Utility-Klassen stehen im Markup immer ausgeschrieben, nie dynamisch zusammengesetzt (der Scanner liest die `.razor`-Dateien als Text und findet nur ganze Klassennamen). Wiederkehrende Bausteine (`btn`, `chip`, `panel`, `hex`, `feed` …) sind Komponentenklassen in `@layer components` in `Styles/app.css`.
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
- Create: `src/Webionic.ICalMerger/Styles/app.css` (Tailwind-Eingabe), `src/Webionic.ICalMerger/wwwroot/css/app.css` (erzeugt, eingecheckt), `.gitattributes`
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
printf '\n# App data\ndata/\n*.db\n*.db-shm\n*.db-wal\n\n# Tailwind-CLI (wird beim Build geladen)\ntools/tailwind/\n' >> .gitignore
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
# Bootstrap und Vorlagen-Styles entfallen komplett (Tailwind ersetzt sie, siehe Step 4b)
rm -rf wwwroot/lib wwwroot/app.css Components/Layout/NavMenu.razor.css Components/Layout/MainLayout.razor.css
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

  <!-- Tailwind CSS v4 Standalone-CLI: kein Node nötig. Die Version ist gepinnt. Die Binärdatei wird beim ersten Build
       nach tools/tailwind/ geladen (gitignored). Schlägt der Download fehl, läuft der Build mit einer Warnung
       weiter und verwendet die eingecheckte wwwroot/css/app.css.
       Mit -p:SkipTailwind=true wird die CSS-Erzeugung übersprungen (für dotnet watch, siehe README). -->
  <PropertyGroup>
    <TailwindVersion>4.3.3</TailwindVersion>
    <TailwindDir>$(MSBuildProjectDirectory)/tools/tailwind/</TailwindDir>
    <TailwindMusl Condition="Exists('/etc/alpine-release')">-musl</TailwindMusl>
    <TailwindAsset Condition="$([MSBuild]::IsOSPlatform('Windows'))">tailwindcss-windows-x64.exe</TailwindAsset>
    <TailwindAsset Condition="$([MSBuild]::IsOSPlatform('OSX')) And '$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)' == 'Arm64'">tailwindcss-macos-arm64</TailwindAsset>
    <TailwindAsset Condition="$([MSBuild]::IsOSPlatform('OSX')) And '$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)' != 'Arm64'">tailwindcss-macos-x64</TailwindAsset>
    <TailwindAsset Condition="$([MSBuild]::IsOSPlatform('Linux')) And '$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)' == 'Arm64'">tailwindcss-linux-arm64$(TailwindMusl)</TailwindAsset>
    <TailwindAsset Condition="$([MSBuild]::IsOSPlatform('Linux')) And '$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)' != 'Arm64'">tailwindcss-linux-x64$(TailwindMusl)</TailwindAsset>
    <TailwindExe>$(TailwindDir)$(TailwindVersion)-$(TailwindAsset)</TailwindExe>
  </PropertyGroup>

  <ItemGroup>
    <None Remove="tools/**" />
  </ItemGroup>

  <Target Name="TailwindDownload" Condition="'$(SkipTailwind)' != 'true' And !Exists('$(TailwindExe)')">
    <DownloadFile SourceUrl="https://github.com/tailwindlabs/tailwindcss/releases/download/v$(TailwindVersion)/$(TailwindAsset)"
                  DestinationFolder="$(TailwindDir)" DestinationFileName="$(TailwindVersion)-$(TailwindAsset)"
                  Retries="3" ContinueOnError="WarnAndContinue" />
    <Exec Condition="Exists('$(TailwindExe)') And !$([MSBuild]::IsOSPlatform('Windows'))" Command="chmod +x &quot;$(TailwindExe)&quot;" />
  </Target>

  <Target Name="TailwindBuild" BeforeTargets="BeforeBuild" DependsOnTargets="TailwindDownload" Condition="'$(SkipTailwind)' != 'true'">
    <Warning Condition="!Exists('$(TailwindExe)')" Text="Tailwind-CLI nicht verfügbar: wwwroot/css/app.css wird nicht neu erzeugt." />
    <Exec Condition="Exists('$(TailwindExe)')" Command="&quot;$(TailwindExe)&quot; -i Styles/app.css -o wwwroot/css/app.css --minify" WorkingDirectory="$(MSBuildProjectDirectory)" />
  </Target>

  <!-- Entwicklung: dotnet msbuild src/Webionic.ICalMerger -t:TailwindWatch (läuft bis Strg+C, nicht minimiert) -->
  <Target Name="TailwindWatch" DependsOnTargets="TailwindDownload">
    <Exec Command="&quot;$(TailwindExe)&quot; -i Styles/app.css -o wwwroot/css/app.css --watch" WorkingDirectory="$(MSBuildProjectDirectory)" />
  </Target>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.0.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.*" PrivateAssets="all" />
  </ItemGroup>

</Project>
```

(Falls die `UserSecretsId` der generierten Vorlage von obiger abweicht, behalte die generierte.)

- [ ] **Step 4b: Tailwind einrichten (statt Bootstrap)**

Die `.csproj` aus Step 4 enthält bereits Download und Build der Tailwind-Standalone-CLI (Version 4.3.3, einzelne Binärdatei, kein Node). Es fehlen die Eingabedatei, die Einbindung und die Git-Attribute.

`src/Webionic.ICalMerger/Styles/app.css` (Eingabe, enthält die Webionic-Tokens, die Basis und die Komponenten für Buttons, Formulare und Meldungen; Task 7b hängt Schriften und die übrigen Bausteine an):

```css
/* Webionic-Theme für den iCal Merger (Tailwind CSS v4).
   Vorlage: docs/mockups/index.html. Tokens: Webionic.Website/DESIGN.md (Navy, Honig-Amber, Sechseck).
   Die Ausgabe wwwroot/css/app.css wird bei jedem Build erzeugt und mit eingecheckt (Task 1, Step 4b).
   Utility-Klassen stehen im Markup immer ausgeschrieben (`class="mb-4 max-w-md"`), nie zusammengesetzt
   (`class="mb-@n"`): Der Scanner liest die .razor-Dateien als Text. Zustandsklassen wie `ok`/`warn`/`err`
   gehören zu den Komponentenklassen unten und werden unabhängig vom Scanner immer ausgegeben. */
@import "tailwindcss" source(none);
@source "../Components/**/*.razor";

@theme {
  --color-navy-950: #081e35;
  --color-navy-900: #0f3458;
  --color-navy-800: #17476f;
  --color-navy-700: #245b86;
  --color-navy-300: #9db6cf;
  --color-navy-100: #dde7f1;
  --color-navy-50: #eff4f9;
  --color-honey-500: #f5a623;
  --color-honey-400: #f8bb4f;
  --color-honey-200: #fde3b0;
  --color-honey-100: #fff1d4;
  --color-ink: #0c2238;
  --color-muted: #4a6076;
  --color-line: #d9e3ed;
  --color-ground: #f4f7fb;
  /* Fehlerrot ausschließlich für Fehlerzustände, keine Akzentfarbe */
  --color-err: #a6261d;
  --color-err-soft: #fdecea;
  --color-warn-ink: #7a4d00;

  --font-display: "Bricolage Grotesque", ui-sans-serif, system-ui, sans-serif;
  --font-sans: "Hanken Grotesk", ui-sans-serif, system-ui, sans-serif;

  --radius-field: 12px;
  --radius-panel: 24px;
  --shadow-soft: 0 1px 2px rgb(8 20 35 / 0.06), 0 12px 32px -12px rgb(8 20 35 / 0.2);
}

@layer base {
  body { margin: 0; background: var(--color-ground); color: var(--color-ink); font: 400 1rem/1.5 var(--font-sans); }
  h1, h2, h3 { font-family: var(--font-display); letter-spacing: -.02em; color: var(--color-navy-900); }
  h1 { font-size: 2rem; font-weight: 700; line-height: 1.1; margin: 0 0 1rem; }
  h1:focus { outline: none; }
  p { margin: 0 0 1rem; }
  main a:not([class]) { color: var(--color-navy-700); text-decoration: underline; text-underline-offset: 3px; }
  code { font: 500 .875rem ui-monospace, SFMono-Regular, Menlo, monospace; color: inherit; }
  ::selection { background: var(--color-honey-200); color: var(--color-navy-950); }
  * { scrollbar-color: var(--color-navy-300) transparent; }

  /* Fokus: Navy statt Amber, weil Amber auf Weiß unter 3:1 Kontrast liegt. Auf dunklen Flächen Amber (siehe Komponenten). */
  :focus-visible { outline: 3px solid var(--color-navy-900); outline-offset: 2px; }

  /* Blazor-Standardflächen */
  .valid.modified:not([type=checkbox]) { outline: 0; }
  .invalid { outline: 2px solid var(--color-err); }
  .validation-message { color: var(--color-err); font-size: .875rem; margin-top: 4px; }
  .validation-errors { color: var(--color-err); margin: 0 0 16px; padding-left: 1.25rem; list-style: disc; font-size: .875rem; }
  .blazor-error-boundary { background: var(--color-err); color: #fff; padding: 1rem; }
  .blazor-error-boundary::after { content: "Es ist ein Fehler aufgetreten."; }
  #blazor-error-ui {
    color-scheme: light only; background: var(--color-honey-100); color: var(--color-navy-950);
    bottom: 0; box-shadow: 0 -1px 2px rgb(8 20 35 / .2); box-sizing: border-box; display: none;
    left: 0; padding: .6rem 1.25rem .7rem; position: fixed; width: 100%; z-index: 1000;
  }
  #blazor-error-ui .dismiss { cursor: pointer; position: absolute; right: .75rem; top: .5rem; background: none; border: 0; font-weight: 600; text-decoration: underline; }
}

@layer components {
  /* Buttons */
  .btn {
    display: inline-flex; align-items: center; justify-content: center; gap: 8px; height: 44px; padding: 0 22px;
    border: 0; border-radius: 9999px; font: 600 .9375rem var(--font-sans); cursor: pointer; text-decoration: none; white-space: nowrap;
  }
  .btn:disabled, .btn.disabled { opacity: .55; cursor: not-allowed; }
  .btn-lg { height: 52px; font-size: 1rem; }
  .btn-primary { background: var(--color-honey-500); color: var(--color-navy-950); }
  .btn-primary:hover:not(:disabled) { background: var(--color-honey-400); color: var(--color-navy-950); }
  .btn-navy { background: var(--color-navy-900); color: #fff; }
  .btn-navy:hover:not(:disabled) { background: var(--color-navy-800); color: #fff; }
  .btn-quiet { background: transparent; color: var(--color-navy-900); box-shadow: inset 0 0 0 1.5px var(--color-navy-300); }
  .btn-quiet:hover:not(:disabled) { background: var(--color-navy-50); color: var(--color-navy-900); }
  .btn-danger { background: transparent; color: var(--color-err); box-shadow: inset 0 0 0 1.5px #e3b3ae; }
  .btn-danger:hover:not(:disabled) { background: var(--color-err-soft); color: var(--color-err); }
  .btn-err { background: var(--color-err); color: #fff; }
  .btn-err:hover:not(:disabled) { background: #8c1f17; color: #fff; }
  .btn-sm { height: 36px; padding: 0 16px; font-size: .875rem; }

  /* Formularfelder und Meldungen */
  .input {
    display: block; height: 48px; width: 100%; border-radius: var(--radius-field); border: 1.5px solid var(--color-navy-300);
    padding: 0 14px; font: 400 1rem var(--font-sans); color: var(--color-ink); background: #fff;
  }
  .input::placeholder { color: var(--color-muted); }
  .input:focus { border-color: var(--color-navy-700); }
  .field-label { display: block; font-weight: 600; font-size: .875rem; margin-bottom: 6px; }
  .notice { border-radius: 16px; padding: 12px 18px; margin: 0 0 20px; background: var(--color-navy-50); color: var(--color-navy-950); }
  .notice.err { background: var(--color-err-soft); color: var(--color-err); }
  .notice.ok { background: var(--color-navy-50); color: var(--color-navy-950); }
}
```

Repo-Root `.gitattributes` (die erzeugte Datei soll in Diffs nicht stören):

```
src/Webionic.ICalMerger/wwwroot/css/app.css linguist-generated=true -diff
```

`Components/App.razor`: Lies die Datei und ändere nur diese Stellen. `<html lang="en">` wird `<html lang="de">`. Die Zeile mit `lib/bootstrap/dist/css/bootstrap.min.css` wird gelöscht. Die Zeile `<link rel="stylesheet" href="@Assets["app.css"]" />` wird zu `<link rel="stylesheet" href="@Assets["css/app.css"]" />`. Alles andere (`ResourcePreloader`, `styles.css`, `ImportMap`, `HeadOutlet`) bleibt.

Erste Erzeugung und Kontrolle (lädt die CLI, ca. 80 MB, einmalig). Es wird nur das Tailwind-Target ausgeführt, denn die App ist an dieser Stelle noch nicht vollständig übersetzbar:

```bash
mkdir -p src/Webionic.ICalMerger/wwwroot/css
dotnet msbuild src/Webionic.ICalMerger -t:TailwindBuild
ls -la src/Webionic.ICalMerger/tools/tailwind src/Webionic.ICalMerger/wwwroot/css
grep -c "btn-primary" src/Webionic.ICalMerger/wwwroot/css/app.css
```

Expected: `tools/tailwind/4.3.3-tailwindcss-<plattform>` existiert und ist ausführbar, `wwwroot/css/app.css` ist angelegt und enthält `btn-primary` (Ausgabe `1`, weil die minimierte Datei nur wenige Zeilen hat). Erscheint die Warnung „Tailwind-CLI nicht verfügbar“, prüfe die Netzwerkverbindung zu `github.com`. Die Seiten aus Step 9 und 10 sind noch nicht geschrieben, `max-w-md` und die übrigen Utilities erscheinen erst nach Step 10 in der Ausgabe (Step 11 prüft das).

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

In `Components/Account/Shared/StatusMessage.razor` ersetze `DisplayMessage.StartsWith("Error")` durch `DisplayMessage.StartsWith("Fehler")` und ersetze das Bootstrap-Element `<div class="alert alert-@statusMessageClass" role="alert">` durch `<div class="notice @(statusMessageClass == "danger" ? "err" : "ok")" role="alert">` (der Variablenname kann in der generierten Vorlage abweichen, die Logik bleibt: Text beginnt mit „Fehler“ = `err`, sonst `ok`).

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
<div class="max-w-md">
    <StatusMessage Message="@errorMessage" />
    <EditForm Model="Input" method="post" OnValidSubmit="LoginUser" FormName="login">
        <DataAnnotationsValidator />
        <ValidationSummary role="alert" />
        <div class="mb-4">
            <label for="Input.Email" class="field-label">E-Mail</label>
            <InputText @bind-Value="Input.Email" id="Input.Email" class="input" autocomplete="username" aria-required="true" placeholder="name@example.com" />
            <ValidationMessage For="() => Input.Email" />
        </div>
        <div class="mb-4">
            <label for="Input.Password" class="field-label">Passwort</label>
            <InputText type="password" @bind-Value="Input.Password" id="Input.Password" class="input" autocomplete="current-password" aria-required="true" placeholder="Passwort" />
            <ValidationMessage For="() => Input.Password" />
        </div>
        <label class="mb-4 flex items-center gap-2 font-medium">
            <InputCheckbox @bind-Value="Input.RememberMe" class="size-4 accent-navy-900" />
            Angemeldet bleiben
        </label>
        <button type="submit" class="btn btn-primary btn-lg w-full">Anmelden</button>
    </EditForm>
    <p class="mt-4 text-muted">Passwort vergessen? Bitte melde dich beim Admin, er schickt dir einen neuen Link.</p>
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
<div class="max-w-md">
    <StatusMessage Message="@Message" />
    <EditForm Model="Input" FormName="reset-password" OnValidSubmit="OnValidSubmitAsync" method="post">
        <DataAnnotationsValidator />
        <ValidationSummary role="alert" />

        <input type="hidden" name="Input.Code" value="@Input.Code" />
        <div class="mb-4">
            <label for="Input.Email" class="field-label">E-Mail</label>
            <InputText @bind-Value="Input.Email" id="Input.Email" class="input" autocomplete="username" aria-required="true" placeholder="name@example.com" />
            <ValidationMessage For="() => Input.Email" />
        </div>
        <div class="mb-4">
            <label for="Input.Password" class="field-label">Passwort</label>
            <InputText type="password" @bind-Value="Input.Password" id="Input.Password" class="input" autocomplete="new-password" aria-required="true" placeholder="Passwort" />
            <ValidationMessage For="() => Input.Password" />
        </div>
        <div class="mb-4">
            <label for="Input.ConfirmPassword" class="field-label">Passwort wiederholen</label>
            <InputText type="password" @bind-Value="Input.ConfirmPassword" id="Input.ConfirmPassword" class="input" autocomplete="new-password" aria-required="true" placeholder="Passwort wiederholen" />
            <ValidationMessage For="() => Input.ConfirmPassword" />
        </div>
        <button type="submit" class="btn btn-primary btn-lg w-full">Passwort speichern</button>
    </EditForm>
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
<p class="text-err">Dieses Konto ist gesperrt. Bitte später erneut versuchen oder den Admin fragen.</p>
```

`AccessDenied.razor`:

```razor
@page "/Account/AccessDenied"
<PageTitle>Kein Zugriff</PageTitle>

<h1>Kein Zugriff</h1>
<p class="text-err">Du hast keine Berechtigung für diese Seite.</p>
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
<div class="max-w-md">
    <StatusMessage Message="@message" />
    <EditForm Model="Input" FormName="change-password" OnValidSubmit="OnValidSubmitAsync" method="post">
        <DataAnnotationsValidator />
        <ValidationSummary role="alert" />
        <div class="mb-4">
            <label for="Input.OldPassword" class="field-label">Aktuelles Passwort</label>
            <InputText type="password" @bind-Value="Input.OldPassword" id="Input.OldPassword" class="input" autocomplete="current-password" aria-required="true" placeholder="Aktuelles Passwort" />
            <ValidationMessage For="() => Input.OldPassword" />
        </div>
        <div class="mb-4">
            <label for="Input.NewPassword" class="field-label">Neues Passwort</label>
            <InputText type="password" @bind-Value="Input.NewPassword" id="Input.NewPassword" class="input" autocomplete="new-password" aria-required="true" placeholder="Neues Passwort" />
            <ValidationMessage For="() => Input.NewPassword" />
        </div>
        <div class="mb-4">
            <label for="Input.ConfirmPassword" class="field-label">Neues Passwort wiederholen</label>
            <InputText type="password" @bind-Value="Input.ConfirmPassword" id="Input.ConfirmPassword" class="input" autocomplete="new-password" aria-required="true" placeholder="Neues Passwort wiederholen" />
            <ValidationMessage For="() => Input.ConfirmPassword" />
        </div>
        <button type="submit" class="btn btn-primary btn-lg w-full">Passwort ändern</button>
    </EditForm>
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

`Components/Layout/NavMenu.razor` (einfache Kopfzeile mit Tailwind-Utilities; Task 7b ersetzt sie durch die Topbar):

```razor
@implements IDisposable

@inject NavigationManager NavigationManager

<header class="flex flex-wrap items-center gap-x-6 gap-y-2 border-b border-line bg-white px-4 py-3 sm:px-8">
    <a href="" class="font-display text-xl font-bold tracking-tight text-navy-900 no-underline">iCal Merger</a>
    <AuthorizeView>
        <Authorized>
            <nav class="flex flex-wrap items-center gap-1 font-semibold" aria-label="Hauptnavigation">
                <NavLink class="rounded-full px-3.5 py-2 text-navy-900 no-underline hover:bg-navy-50" ActiveClass="bg-navy-50" href="" Match="NavLinkMatch.All">Meine Kalender</NavLink>
                <AuthorizeView Roles="Admin" Context="adminContext">
                    <NavLink class="rounded-full px-3.5 py-2 text-navy-900 no-underline hover:bg-navy-50" ActiveClass="bg-navy-50" href="admin/users">Nutzer</NavLink>
                </AuthorizeView>
            </nav>
            <div class="ml-auto flex items-center gap-3 text-sm">
                <NavLink class="text-muted no-underline hover:underline" href="Account/Manage/ChangePassword">@context.User.Identity?.Name</NavLink>
                <form action="Account/Logout" method="post">
                    <AntiforgeryToken />
                    <input type="hidden" name="ReturnUrl" value="@currentUrl" />
                    <button type="submit" class="btn btn-quiet btn-sm">Abmelden</button>
                </form>
            </div>
        </Authorized>
        <NotAuthorized>
            <NavLink class="ml-auto rounded-full px-3.5 py-2 font-semibold text-navy-900 no-underline hover:bg-navy-50" href="Account/Login">Anmelden</NavLink>
        </NotAuthorized>
    </AuthorizeView>
</header>

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

<NavMenu />

<main class="mx-auto max-w-5xl px-4 pb-16 pt-8 sm:px-8 sm:pt-10">
    @Body
</main>

<div id="blazor-error-ui" data-nosnippet>
    Es ist ein unerwarteter Fehler aufgetreten.
    <a href=".">Neu laden</a>
    <button type="button" class="dismiss">Schließen</button>
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

Tailwind prüfen: Der Build hat `wwwroot/css/app.css` jetzt mit den Utilities der Seiten neu erzeugt.

```bash
grep -c "max-w-md" src/Webionic.ICalMerger/wwwroot/css/app.css
grep -rci bootstrap src/Webionic.ICalMerger/Components src/Webionic.ICalMerger/wwwroot | grep -v ':0' || echo "kein Bootstrap"
```

Expected: `1` und „kein Bootstrap“. Findet die erste Zeile `max-w-md` nicht, liest der Scanner die `.razor`-Dateien nicht (`@source` in `Styles/app.css` prüfen).

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
- Create: `src/Webionic.ICalMerger/Data/MergedCalendar.cs`, `Data/CalendarSource.cs`, `Data/SourceStatus.cs`, `Data/DesignTimeDbContextFactory.cs`, `Data/Migrations/*` (generiert)
- Modify: `src/Webionic.ICalMerger/Data/ApplicationDbContext.cs`
- Create: `src/Webionic.ICalMerger/Calendars/CalendarService.cs`, `Calendars/AppLimits.cs`, `Calendars/DomainException.cs`, `Calendars/TokenGenerator.cs`
- Modify: `src/Webionic.ICalMerger/ServiceRegistration.cs`
- Create: `tests/Webionic.ICalMerger.Tests/Support/TestDb.cs`, `Support/FakeFetcher.cs`
- Test: `tests/Webionic.ICalMerger.Tests/Data/MigrationsTests.cs`, `tests/Webionic.ICalMerger.Tests/Data/SourceStatusTests.cs`, `tests/Webionic.ICalMerger.Tests/Calendars/CalendarServiceTests.cs`

**Interfaces:**
- Consumes: `ICalendarFetcher`, `FetchException`, `SafeHttpFetcher.NormalizeUrl`, `IcsMerger.LooksLikeCalendar`, `ApplicationDbContext`.
- Produces:
  - Entities `MergedCalendar { int Id; string OwnerId; ApplicationUser? Owner; string Name; string Token; DateTime CreatedAt; List<CalendarSource> Sources }` und `CalendarSource { int Id; int MergedCalendarId; MergedCalendar? MergedCalendar; string Name; string Url; int SortOrder; DateTime? LastAttemptAt; DateTime? LastSuccessAt; string? LastError }` (alle `DateTime` in UTC).
  - `ApplicationDbContext.Calendars`, `ApplicationDbContext.Sources`
  - `enum SourceStatus { Unknown, Ok, Stale, Error }`, `static SourceStatus SourceStatusExtensions.GetStatus(this CalendarSource)` und `static SourceStatus SourceStatusExtensions.Summarize(IEnumerable<CalendarSource>)` (schlechtester Status). Ableitung aus den vorhandenen Spalten, ohne Schemaänderung: kein Fehler und nie erfolgreich = `Unknown`; kein Fehler und erfolgreich = `Ok`; Fehler, aber früher erfolgreich = `Stale` (der Feed liefert weiter die letzten bekannten Daten); Fehler und nie erfolgreich = `Error`. Die UI (Task 7b, 8) nutzt das für Sechseck-Farbe und Statustext.
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

- [ ] **Step 1b: Statusableitung für die UI**

Die Spalten `LastAttemptAt`, `LastSuccessAt` und `LastError` reichen für die drei Statusstufen der UI. Es braucht nur eine reine Ableitung. Der Test kommt zuerst.

`tests/Webionic.ICalMerger.Tests/Data/SourceStatusTests.cs`:

```csharp
using Webionic.ICalMerger.Data;

namespace Webionic.ICalMerger.Tests.Data;

public sealed class SourceStatusTests
{
    private static readonly DateTime Earlier = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);

    private static CalendarSource Source(string? error, DateTime? success) => new() { LastError = error, LastSuccessAt = success };

    [Fact]
    public void NeverFetched_IsUnknown() => Assert.Equal(SourceStatus.Unknown, Source(null, null).GetStatus());

    [Fact]
    public void FetchedWithoutError_IsOk() => Assert.Equal(SourceStatus.Ok, Source(null, Earlier).GetStatus());

    [Fact]
    public void ErrorAfterEarlierSuccess_IsStale() => Assert.Equal(SourceStatus.Stale, Source("HTTP 503", Earlier).GetStatus());

    [Fact]
    public void ErrorWithoutAnySuccess_IsError() => Assert.Equal(SourceStatus.Error, Source("HTTP 500", null).GetStatus());

    [Fact]
    public void Summarize_ReturnsWorstStatus()
    {
        var ok = Source(null, Earlier);
        var stale = Source("x", Earlier);
        var error = Source("y", null);
        var unknown = Source(null, null);

        Assert.Equal(SourceStatus.Error, SourceStatusExtensions.Summarize([ok, stale, error, unknown]));
        Assert.Equal(SourceStatus.Stale, SourceStatusExtensions.Summarize([ok, stale, unknown]));
        Assert.Equal(SourceStatus.Ok, SourceStatusExtensions.Summarize([ok, unknown]));
        Assert.Equal(SourceStatus.Unknown, SourceStatusExtensions.Summarize([unknown]));
        Assert.Equal(SourceStatus.Unknown, SourceStatusExtensions.Summarize([]));
    }
}
```

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~SourceStatusTests"`
Expected: Build-Fehler (`SourceStatus` fehlt).

`Data/SourceStatus.cs`:

```csharp
namespace Webionic.ICalMerger.Data;

public enum SourceStatus
{
    /// <summary>Noch nie abgerufen.</summary>
    Unknown,
    /// <summary>Letzter Abruf erfolgreich.</summary>
    Ok,
    /// <summary>Letzter Abruf fehlgeschlagen, aber früher erfolgreich: der Feed liefert die letzten bekannten Daten.</summary>
    Stale,
    /// <summary>Fehler und noch nie erfolgreich: die Quelle fehlt im Feed.</summary>
    Error,
}

public static class SourceStatusExtensions
{
    public static SourceStatus GetStatus(this CalendarSource source) => (source.LastError, source.LastSuccessAt) switch
    {
        (null, null) => SourceStatus.Unknown,
        (null, _) => SourceStatus.Ok,
        (_, null) => SourceStatus.Error,
        _ => SourceStatus.Stale,
    };

    /// <summary>Der schlechteste Status gewinnt. Ohne Quellen oder ohne jeden Abruf: <see cref="SourceStatus.Unknown"/>.</summary>
    public static SourceStatus Summarize(IEnumerable<CalendarSource> sources)
    {
        var statuses = sources.Select(GetStatus).ToList();
        if (statuses.Contains(SourceStatus.Error)) return SourceStatus.Error;
        if (statuses.Contains(SourceStatus.Stale)) return SourceStatus.Stale;
        if (statuses.Contains(SourceStatus.Ok)) return SourceStatus.Ok;
        return SourceStatus.Unknown;
    }
}
```

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~SourceStatusTests"`
Expected: alle PASS. (`GetStatus` ist eine reine Eigenschaft der Daten; EF Core bildet sie nicht ab, die Migration in Step 2 bleibt unverändert.)

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
  - `sealed record UserRow(string Id, string Email, bool IsAdmin, bool IsLockedOut, bool HasPassword, int CalendarCount)`
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
        Assert.Equal(0, rows.Single(r => r.Id != withCalendars).CalendarCount);
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

### Task 7b: Webionic-Theme, Layout und UI-Bausteine

Referenz ist das abgenommene Mockup `docs/mockups/index.html` (Markup und CSS dort sind die Vorlage), der Produktkontext steht in `PRODUCT.md`, die Markenwerte kommen aus `/home/benedikt/_DEV/Webionic.Website/DESIGN.md`.

**Entscheidung zu Tailwind:** Bootstrap entfällt komplett (Task 1, Step 4b). Tokens (Farben, Schriften, Radien, Schatten) stehen im `@theme`-Block von `Styles/app.css` und erzeugen die Utilities (`text-navy-900`, `bg-honey-500`, `font-display`, `shadow-soft` …). Einmalige Layout-Details stehen als Utility-Klassen im Markup. Wiederkehrende, zusammengesetzte Bausteine (`btn`, `chip`, `panel`, `hex`, `feed`, `cal`, `src`, `menu`, `dialog` …) sind Komponentenklassen in `@layer components`, weil sie Zustände, Pseudo-Elemente und Handy-Varianten bündeln und in Task 8 und 9 an vielen Stellen vorkommen. Die Tailwind-Ausgabe `wwwroot/css/app.css` wird bei jedem Build erzeugt und eingecheckt (Begründung in den Global Constraints). Die Webionic-Schriften werden per `@font-face` am Ende von `Styles/app.css` eingebunden.

**Files:**
- Modify: `src/Webionic.ICalMerger/Styles/app.css` (Schriften und Komponentenklassen anhängen)
- Create: `src/Webionic.ICalMerger/wwwroot/fonts/bricolage-grotesque-latin-wght-normal.woff2`, `wwwroot/fonts/hanken-grotesk-latin-wght-normal.woff2`, `wwwroot/img/logo-new-light.svg`; erzeugt: `wwwroot/css/app.css`
- Create: `src/Webionic.ICalMerger/Components/Ui/Icon.razor`, `IconSprite.razor`, `StatusHex.razor`, `ConfirmDialog.razor`, `MoreMenu.razor`, `RelativeTime.cs`, `StatusText.cs`
- Create: `src/Webionic.ICalMerger/Components/Layout/Topbar.razor`
- Modify: `Components/Layout/MainLayout.razor`, `Components/App.razor`, `Components/_Imports.razor`
- Delete: `Components/Layout/NavMenu.razor` (Kopfzeile aus Task 1, ersetzt durch `Topbar.razor`)
- Test: `tests/Webionic.ICalMerger.Tests/Ui/RelativeTimeTests.cs`, `tests/Webionic.ICalMerger.Tests/Ui/StatusTextTests.cs`, `tests/Webionic.ICalMerger.Tests/Ui/ThemeTests.cs`

**Interfaces:**
- Consumes: `SourceStatus`, `GetStatus()`, `SourceStatusExtensions.Summarize` (Task 4), `AppFactory` (Task 6).
- Produces (Namespace `Webionic.ICalMerger.Components.Ui`):
  - `static string RelativeTime.Ago(DateTime? utc, DateTime nowUtc)`: „noch nie“, „gerade eben“, „vor 3 Min.“, „vor 2 Std.“, „vor 1 Tag“, „vor 3 Tagen“, danach „am 01.10.2026“.
  - `readonly record struct StatusLine(SourceStatus Status, string Text, string? Detail = null)` und `static class StatusText` mit `StatusLine Source(CalendarSource source, DateTime nowUtc)`, `StatusLine Calendar(IReadOnlyCollection<CalendarSource> sources, DateTime nowUtc)`, `string Css(SourceStatus status)` (`ok`, `warn`, `err`, `off`).
  - Komponenten `<Icon Name="…" />`, `<IconSprite />`, `<StatusHex Status="…" />`, `<ConfirmDialog Title ConfirmText Busy OnConfirm OnCancel>Text</ConfirmDialog>`, `<MoreMenu Label="…">Einträge als <button role="menuitem"></MoreMenu>`.
  - Komponentenklassen für die Seiten aus Task 8 und 9 (siehe `Styles/app.css`).

- [ ] **Step 1: Assets übernehmen**

Fonts und Logo stammen aus dem Webionic-Website-Repo beziehungsweise dem Mockup (beide variable `woff2`, Latin inklusive Umlauten, selbst gehostet, kein Google-Fonts-Aufruf zur Laufzeit):

```bash
W=/home/benedikt/_DEV/Webionic.Website/src/OrchardCore.Webionic.Theme/wwwroot/fonts
A=src/Webionic.ICalMerger/wwwroot
mkdir -p $A/fonts $A/img
cp $W/bricolage-grotesque-latin-wght-normal.woff2 $W/hanken-grotesk-latin-wght-normal.woff2 $A/fonts/
cp docs/mockups/logo-new-light.svg $A/img/
rm -f src/Webionic.ICalMerger/Components/Layout/NavMenu.razor
ls $A/fonts $A/img
```

Expected: beide `woff2`-Dateien und `logo-new-light.svg` sind vorhanden.

- [ ] **Step 2: Schriften und Komponentenklassen an `Styles/app.css` anhängen**

Hänge den folgenden Block ans Ende von `src/Webionic.ICalMerger/Styles/app.css` an (nach dem `@layer components`-Block aus Task 1; mehrere Layer-Blöcke gleichen Namens werden von Tailwind zusammengeführt). `@font-face` steht bewusst außerhalb der Layer, die Pfade sind relativ zur Ausgabedatei `wwwroot/css/app.css`.

```css
@font-face {
  font-family: "Bricolage Grotesque";
  src: url("../fonts/bricolage-grotesque-latin-wght-normal.woff2") format("woff2");
  font-weight: 200 800;
  font-display: swap;
}
@font-face {
  font-family: "Hanken Grotesk";
  src: url("../fonts/hanken-grotesk-latin-wght-normal.woff2") format("woff2");
  font-weight: 100 900;
  font-display: swap;
}

@layer components {
  .linkbtn { background: none; border: 0; padding: 0; color: var(--color-honey-400); font: 600 .875rem var(--font-sans); text-decoration: underline; text-underline-offset: 3px; cursor: pointer; }
  .icon-btn { width: 36px; height: 36px; border-radius: 10px; border: 0; background: transparent; color: var(--color-navy-700); display: grid; place-items: center; cursor: pointer; }
  .icon-btn:hover:not(:disabled) { background: var(--color-navy-50); }
  .icon-btn:disabled { opacity: .35; cursor: not-allowed; }
  svg.i { width: 18px; height: 18px; stroke: currentColor; fill: none; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; flex: none; }

  /* Formularzeilen */
  .inline { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
  .inline > .input { flex: 1 1 260px; width: auto; }

  .feed :focus-visible, .scrim :focus-visible { outline-color: var(--color-honey-400); }

  /* Topbar und Layout */
  .topbar {
    background: #fff; border-bottom: 1px solid var(--color-line); display: flex; align-items: center;
    gap: 24px; padding: 0 32px; min-height: 68px; position: relative;
  }
  .topbar-brand img { height: 30px; display: block; }
  .nav-toggle, .burger { display: none; }
  .topbar-menu { display: flex; align-items: center; gap: 24px; flex: 1; }
  .topbar nav { display: flex; gap: 4px; }
  .topbar nav a { padding: 8px 14px; border-radius: 9999px; font-weight: 600; font-size: .9375rem; color: var(--color-navy-900); text-decoration: none; }
  .topbar nav a:hover { background: var(--color-navy-50); }
  .topbar nav a.active { background: var(--color-navy-50); }
  .who { margin-left: auto; display: flex; align-items: center; gap: 12px; font-size: .875rem; }
  .who-name { color: var(--color-muted); }
  .who form { margin: 0; }
  .main { max-width: 1040px; margin: 0 auto; padding: 40px 32px 64px; }

  .pagehead { display: flex; align-items: flex-end; justify-content: space-between; gap: 16px; margin-bottom: 28px; flex-wrap: wrap; }
  h1.page { font: 700 2rem/1.1 var(--font-display); letter-spacing: -.02em; color: var(--color-navy-900); margin: 0; }
  .sub { color: var(--color-muted); font-size: .9375rem; margin: 6px 0 0; }
  .back { display: inline-flex; align-items: center; gap: 6px; color: var(--color-muted); font-weight: 600; font-size: .875rem; text-decoration: none; margin-bottom: 12px; }
  .panel { background: #fff; border: 1px solid var(--color-line); border-radius: 24px; padding: 28px; }
  .sec-head { display: flex; justify-content: space-between; align-items: center; margin-bottom: 6px; gap: 12px; }
  .sec-head h2 { font: 700 1.25rem var(--font-display); margin: 0; }
  .sec-sub { color: var(--color-muted); font-size: .875rem; margin: 0 0 16px; }

  /* Sechseck-Status */
  .hex { width: 22px; height: 19px; clip-path: polygon(25% 0, 75% 0, 100% 50%, 75% 100%, 25% 100%, 0 50%); display: inline-block; flex: none; }
  .hex.ok { background: var(--color-navy-900); }
  .hex.warn { background: var(--color-honey-500); }
  .hex.err { background: var(--color-err); }
  .hex.off { background: var(--color-navy-300); }

  /* Kalenderliste */
  .cals { display: grid; gap: 12px; }
  .cal {
    background: #fff; border: 1px solid var(--color-line); border-radius: 24px; padding: 20px 24px;
    display: grid; grid-template-columns: auto 1fr auto auto; gap: 20px; align-items: center; text-decoration: none; color: inherit;
  }
  .cal:hover { box-shadow: var(--shadow-soft); border-color: var(--color-navy-100); }
  .cal.problem { border-color: #efc4c0; }
  .cal h2 { font: 700 1.25rem/1.3 var(--font-display); margin: 0; }
  .cal .meta { color: var(--color-muted); font-size: .875rem; margin-top: 2px; }
  .cal .chev { color: var(--color-navy-300); display: grid; }
  .state { font-weight: 600; font-size: .875rem; white-space: nowrap; }
  .state.err { color: var(--color-err); }
  .state.ok { color: var(--color-navy-900); }
  .state.warn { color: var(--color-warn-ink); }
  .state.off { color: var(--color-muted); }
  .legend { display: flex; gap: 20px; flex-wrap: wrap; color: var(--color-muted); font-size: .875rem; margin: 16px 0 0; padding: 0; list-style: none; }
  .legend li { display: inline-flex; gap: 8px; align-items: center; }
  .create { margin-bottom: 20px; }

  /* Feed-Block */
  .feed { background: var(--color-navy-950); color: var(--color-navy-100); border-radius: 24px; padding: 24px 28px; margin-bottom: 20px; }
  .feed h2 { font: 700 1.25rem var(--font-display); margin: 0 0 4px; color: #fff; }
  .feed p { margin: 0 0 16px; color: var(--color-navy-300); font-size: .9375rem; }
  .feedbox { display: flex; gap: 12px; align-items: center; background: rgb(255 255 255 / .07); border-radius: 16px; padding: 8px 8px 8px 18px; }
  .feedbox code { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: .9375rem; color: #fff; }
  .feed .tools { display: flex; gap: 16px; margin-top: 14px; font-size: .875rem; color: var(--color-navy-300); align-items: center; flex-wrap: wrap; }

  /* Quellenliste */
  .src { display: grid; grid-template-columns: 28px 22px 1fr minmax(0, 18rem) auto; gap: 14px; align-items: center; padding: 14px 0; border-top: 1px solid var(--color-line); }
  .src:first-of-type { border-top: 0; }
  .src .rank { font: 700 .9375rem var(--font-display); color: var(--color-navy-700); text-align: center; }
  .src .name { font-weight: 600; overflow-wrap: anywhere; }
  .src .u { color: var(--color-muted); font-size: .8125rem; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; overflow-wrap: anywhere; }
  .src .st { font-size: .8125rem; color: var(--color-muted); text-align: right; }
  .src .detail { font-weight: 400; }
  .src .mst, .src .src-more { display: none; }
  .src.bad, .src.stale { margin: 0 -16px; padding: 14px 16px; border-radius: 16px; border-top-color: transparent; }
  .src.bad { background: var(--color-err-soft); }
  .src.stale { background: var(--color-honey-100); }
  .src.bad .st, .src.bad .mst { color: var(--color-err); font-weight: 600; }
  .src.stale .st, .src.stale .mst { color: var(--color-warn-ink); font-weight: 600; }
  .src.bad + .src, .src.stale + .src { border-top-color: transparent; }
  .src .acts { display: flex; gap: 2px; }
  .src-edit { grid-column: 1 / -1; display: grid; grid-template-columns: 1fr 2fr auto; gap: 12px; align-items: center; }
  .src-edit .btns { display: flex; gap: 8px; }
  .add { display: grid; grid-template-columns: 1fr 2fr auto; gap: 12px; margin-top: 18px; padding-top: 20px; border-top: 1px solid var(--color-line); }
  .danger-zone { display: flex; justify-content: space-between; align-items: center; gap: 16px; margin-top: 20px; flex-wrap: wrap; }
  .danger-zone p { margin: 0; color: var(--color-muted); font-size: .875rem; }

  /* Mehr-Menü */
  .menu { position: relative; display: inline-block; }
  .menu-backdrop { position: fixed; inset: 0; z-index: 10; }
  .menu-list {
    position: absolute; right: 0; top: calc(100% + 4px); z-index: 20; min-width: 210px; background: #fff;
    border: 1px solid var(--color-line); border-radius: 16px; box-shadow: var(--shadow-soft); padding: 6px; display: flex; flex-direction: column;
  }
  .menu-list button { height: 40px; padding: 0 12px; border: 0; border-radius: 10px; background: none; text-align: left; font: 600 .9375rem var(--font-sans); color: var(--color-navy-900); cursor: pointer; }
  .menu-list button:hover:not(:disabled) { background: var(--color-navy-50); }
  .menu-list button:disabled { opacity: .45; cursor: not-allowed; }
  .menu-list button.danger { color: var(--color-err); }

  /* Dialog */
  .scrim { position: fixed; inset: 0; z-index: 50; background: rgb(8 30 53 / .55); padding: 24px; display: grid; place-items: center; }
  .dialog { background: #fff; border-radius: 24px; padding: 28px; max-width: 440px; width: 100%; box-shadow: var(--shadow-soft); }
  .dialog h2 { font: 700 1.375rem var(--font-display); margin: 0 0 8px; }
  .dialog p { margin: 0 0 20px; }
  .dialog .btns { display: flex; gap: 10px; justify-content: flex-end; flex-wrap: wrap; }

  /* Admin */
  .data-table { width: 100%; border-collapse: collapse; }
  .data-table th { text-align: left; font-weight: 600; font-size: .8125rem; color: var(--color-muted); padding: 0 12px 10px; border-bottom: 1px solid var(--color-line); }
  .data-table td { padding: 14px 12px; border-bottom: 1px solid var(--color-line); vertical-align: middle; font-size: .9375rem; overflow-wrap: anywhere; }
  .data-table tr:last-child td { border-bottom: 0; }
  .data-table .actions { display: flex; gap: 8px; justify-content: flex-end; align-items: center; }
  .table-panel { padding: 20px 16px; }
  .chip { display: inline-flex; align-items: center; border-radius: 9999px; padding: 3px 12px; font-weight: 600; font-size: .8125rem; margin-right: 6px; }
  .chip.admin { background: var(--color-navy-900); color: #fff; }
  .chip.user { background: var(--color-navy-50); color: var(--color-navy-900); }
  .chip.invited { background: var(--color-honey-100); color: var(--color-warn-ink); }
  .chip.locked { background: var(--color-err-soft); color: var(--color-err); }
  .invite { background: var(--color-honey-100); border-radius: 20px; padding: 18px 22px; margin-bottom: 20px; display: flex; gap: 16px; align-items: center; flex-wrap: wrap; color: var(--color-navy-950); }
  .invite code { flex: 1 1 320px; min-width: 0; background: #fff; border-radius: 12px; padding: 10px 14px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .invite p { margin: 0; flex: 1 1 100%; color: var(--color-warn-ink); font-size: .875rem; }

  /* Handybreite */
  @media (max-width: 760px) {
    .topbar { padding: 0 16px; flex-wrap: wrap; gap: 0 12px; }
    .burger { display: grid; place-items: center; width: 44px; height: 44px; margin-left: auto; border-radius: 12px; background: var(--color-navy-50); color: var(--color-navy-900); cursor: pointer; }
    .nav-toggle { display: block; position: absolute; opacity: 0; width: 44px; height: 44px; right: 16px; top: 12px; margin: 0; cursor: pointer; }
    .nav-toggle:focus-visible + .burger { outline: 3px solid var(--color-navy-900); outline-offset: 2px; }
    .topbar-menu { display: none; flex-basis: 100%; flex-direction: column; align-items: stretch; gap: 12px; padding: 4px 0 16px; }
    .nav-toggle:checked ~ .topbar-menu { display: flex; }
    .topbar nav { flex-direction: column; }
    .who { margin-left: 0; justify-content: space-between; }
    .main { padding: 24px 16px 40px; }
    h1.page { font-size: 1.625rem; }
    .panel { padding: 18px; border-radius: 20px; }
    .cal { grid-template-columns: auto 1fr; padding: 16px; }
    .cal .chev { display: none; }
    .cal .state { grid-column: 2; white-space: normal; }
    .feed { padding: 20px; }
    .feedbox { flex-direction: column; align-items: stretch; padding: 14px; }
    .src { grid-template-columns: 22px 1fr auto; }
    .src .rank, .src .st, .src .acts { display: none; }
    .src .mst, .src .src-more { display: block; }
    .src .mst { font-size: .8125rem; color: var(--color-muted); }
    .src-edit, .add { grid-template-columns: 1fr; }
    .pagehead .btn-primary { width: 100%; }
    .data-table thead { display: none; }
    .data-table tr { display: grid; gap: 4px; padding: 14px 0; border-bottom: 1px solid var(--color-line); }
    .data-table tr:last-child { border-bottom: 0; }
    .data-table td { display: block; border: 0; padding: 2px 12px; }
    .data-table td[data-label]::before { content: attr(data-label) ": "; color: var(--color-muted); font-size: .8125rem; }
    .data-table .actions { justify-content: flex-start; padding-top: 8px; }
  }

  @media (prefers-reduced-motion: no-preference) {
    .btn, .cal, .icon-btn { transition: background-color .15s ease-out, box-shadow .15s ease-out; }
  }
}
```

Hinweise für alle Seiten: Zustände wie `ok`/`warn`/`err`/`off`, `problem`, `bad`, `stale`, `active` sind Komponentenklassen und dürfen im Markup über `@(…)` zugewiesen werden. Utility-Klassen (`mt-5`, `text-muted`, `tabular-nums` …) dagegen nie aus Teilen zusammensetzen.

Erzeugen und kurz prüfen: `dotnet msbuild src/Webionic.ICalMerger -t:TailwindBuild && grep -c "hex" src/Webionic.ICalMerger/wwwroot/css/app.css` (Expected: `1`).

- [ ] **Step 3: Failing Tests für `RelativeTime` und `StatusText`**

`tests/Webionic.ICalMerger.Tests/Ui/RelativeTimeTests.cs`:

```csharp
using Webionic.ICalMerger.Components.Ui;

namespace Webionic.ICalMerger.Tests.Ui;

public sealed class RelativeTimeTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "gerade eben")]
    [InlineData(59, "gerade eben")]
    [InlineData(60, "vor 1 Min.")]
    [InlineData(3 * 60, "vor 3 Min.")]
    [InlineData(59 * 60, "vor 59 Min.")]
    [InlineData(60 * 60, "vor 1 Std.")]
    [InlineData(23 * 3600, "vor 23 Std.")]
    [InlineData(24 * 3600, "vor 1 Tag")]
    [InlineData(3 * 24 * 3600, "vor 3 Tagen")]
    [InlineData(7 * 24 * 3600, "am 27.09.2026")]
    public void Ago_FormatsAgeInGerman(int secondsAgo, string expected)
    {
        Assert.Equal(expected, RelativeTime.Ago(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void Ago_Null_IsNever() => Assert.Equal("noch nie", RelativeTime.Ago(null, Now));

    [Fact]
    public void Ago_FutureTimestamp_IsJustNow() => Assert.Equal("gerade eben", RelativeTime.Ago(Now.AddMinutes(5), Now));

    [Fact]
    public void Ago_UnspecifiedKind_IsTreatedAsUtc()
    {
        var fromDb = DateTime.SpecifyKind(Now.AddMinutes(-3), DateTimeKind.Unspecified);
        Assert.Equal("vor 3 Min.", RelativeTime.Ago(fromDb, Now));
    }
}
```

`tests/Webionic.ICalMerger.Tests/Ui/StatusTextTests.cs`:

```csharp
using Webionic.ICalMerger.Components.Ui;
using Webionic.ICalMerger.Data;

namespace Webionic.ICalMerger.Tests.Ui;

public sealed class StatusTextTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private static CalendarSource Ok(int minutesAgo = 3) => new() { LastSuccessAt = Now.AddMinutes(-minutesAgo), LastAttemptAt = Now.AddMinutes(-minutesAgo) };
    private static CalendarSource Stale(int hoursAgo = 2) => new() { LastSuccessAt = Now.AddHours(-hoursAgo), LastError = "Zeitüberschreitung" };
    private static CalendarSource Broken() => new() { LastError = "HTTP 500" };

    [Fact]
    public void Source_Ok_ShowsAge()
    {
        var line = StatusText.Source(Ok(), Now);

        Assert.Equal(SourceStatus.Ok, line.Status);
        Assert.Equal("vor 3 Min. abgerufen", line.Text);
        Assert.Null(line.Detail);
    }

    [Fact]
    public void Source_Stale_ShowsErrorAndLastSuccess()
    {
        var line = StatusText.Source(Stale(), Now);

        Assert.Equal(SourceStatus.Stale, line.Status);
        Assert.Equal("Zeitüberschreitung", line.Text);
        Assert.Equal("Letzter Erfolg: vor 2 Std.", line.Detail);
    }

    [Fact]
    public void Source_Error_ShowsErrorAndNeverSucceeded()
    {
        var line = StatusText.Source(Broken(), Now);

        Assert.Equal(SourceStatus.Error, line.Status);
        Assert.Equal("HTTP 500", line.Text);
        Assert.Equal("Noch nie erfolgreich abgerufen", line.Detail);
    }

    [Fact]
    public void Source_Unknown_IsNotFetchedYet()
    {
        var line = StatusText.Source(new CalendarSource(), Now);

        Assert.Equal(SourceStatus.Unknown, line.Status);
        Assert.Equal("Noch nicht abgerufen", line.Text);
    }

    [Fact]
    public void Calendar_WithoutSources_HasNoSources()
    {
        var line = StatusText.Calendar([], Now);

        Assert.Equal(SourceStatus.Unknown, line.Status);
        Assert.Equal("Keine Quellen", line.Text);
    }

    [Fact]
    public void Calendar_AllOk()
    {
        var line = StatusText.Calendar([Ok(), Ok()], Now);

        Assert.Equal(SourceStatus.Ok, line.Status);
        Assert.Equal("Alle Quellen erreichbar", line.Text);
    }

    [Fact]
    public void Calendar_WithBrokenSource_CountsBrokenOnes()
    {
        Assert.Equal("1 Quelle fehlerhaft", StatusText.Calendar([Ok(), Broken(), Stale()], Now).Text);
        Assert.Equal("2 Quellen fehlerhaft", StatusText.Calendar([Broken(), Broken()], Now).Text);
        Assert.Equal(SourceStatus.Error, StatusText.Calendar([Ok(), Broken()], Now).Status);
    }

    [Fact]
    public void Calendar_WithStaleSourcesOnly_ShowsOldestState()
    {
        var line = StatusText.Calendar([Ok(), Stale(hoursAgo: 2), Stale(hoursAgo: 5)], Now);

        Assert.Equal(SourceStatus.Stale, line.Status);
        Assert.Equal("Zwischenspeicher, Stand: vor 5 Std.", line.Text);
    }

    [Theory]
    [InlineData(SourceStatus.Ok, "ok")]
    [InlineData(SourceStatus.Stale, "warn")]
    [InlineData(SourceStatus.Error, "err")]
    [InlineData(SourceStatus.Unknown, "off")]
    public void Css_MapsStatusToClass(SourceStatus status, string expected) => Assert.Equal(expected, StatusText.Css(status));
}
```

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~Ui."`
Expected: Build-Fehler (`RelativeTime`, `StatusText` fehlen).

- [ ] **Step 4: `RelativeTime` und `StatusText` implementieren**

`Components/Ui/RelativeTime.cs`:

```csharp
using System.Globalization;

namespace Webionic.ICalMerger.Components.Ui;

public static class RelativeTime
{
    /// <summary>Relative deutsche Zeitangabe. Absolute Zeiten vermeiden die Zeitzone, die der Container nicht kennt.</summary>
    public static string Ago(DateTime? utc, DateTime nowUtc)
    {
        if (utc is null) return "noch nie";

        var value = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
        var age = nowUtc - value;

        if (age < TimeSpan.FromMinutes(1)) return "gerade eben";
        if (age < TimeSpan.FromHours(1)) return $"vor {(int)age.TotalMinutes} Min.";
        if (age < TimeSpan.FromDays(1)) return $"vor {(int)age.TotalHours} Std.";
        if (age < TimeSpan.FromDays(7))
        {
            var days = (int)age.TotalDays;
            return days == 1 ? "vor 1 Tag" : $"vor {days} Tagen";
        }
        return "am " + value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    }
}
```

`Components/Ui/StatusText.cs`:

```csharp
using Webionic.ICalMerger.Data;

namespace Webionic.ICalMerger.Components.Ui;

public readonly record struct StatusLine(SourceStatus Status, string Text, string? Detail = null);

/// <summary>Deutsche Statustexte für Quellen und Kalender, an einer Stelle und testbar.</summary>
public static class StatusText
{
    public static StatusLine Source(CalendarSource source, DateTime nowUtc)
    {
        var status = source.GetStatus();
        return status switch
        {
            SourceStatus.Ok => new(status, $"{RelativeTime.Ago(source.LastSuccessAt, nowUtc)} abgerufen"),
            SourceStatus.Stale => new(status, source.LastError!, $"Letzter Erfolg: {RelativeTime.Ago(source.LastSuccessAt, nowUtc)}"),
            SourceStatus.Error => new(status, source.LastError!, "Noch nie erfolgreich abgerufen"),
            _ => new(status, "Noch nicht abgerufen"),
        };
    }

    public static StatusLine Calendar(IReadOnlyCollection<CalendarSource> sources, DateTime nowUtc)
    {
        if (sources.Count == 0) return new(SourceStatus.Unknown, "Keine Quellen");

        var status = SourceStatusExtensions.Summarize(sources);
        switch (status)
        {
            case SourceStatus.Error:
                var broken = sources.Count(s => s.GetStatus() == SourceStatus.Error);
                return new(status, broken == 1 ? "1 Quelle fehlerhaft" : $"{broken} Quellen fehlerhaft");
            case SourceStatus.Stale:
                var oldest = sources.Where(s => s.GetStatus() == SourceStatus.Stale).Min(s => s.LastSuccessAt);
                return new(status, $"Zwischenspeicher, Stand: {RelativeTime.Ago(oldest, nowUtc)}");
            case SourceStatus.Ok:
                return new(status, "Alle Quellen erreichbar");
            default:
                return new(status, "Noch nicht abgerufen");
        }
    }

    public static string Css(SourceStatus status) => status switch
    {
        SourceStatus.Ok => "ok",
        SourceStatus.Stale => "warn",
        SourceStatus.Error => "err",
        _ => "off",
    };
}
```

Run: `dotnet test tests/Webionic.ICalMerger.Tests --filter "FullyQualifiedName~Ui."`
Expected: alle PASS.

- [ ] **Step 5: UI-Bausteine**

`Components/Ui/Icon.razor`:

```razor
<svg class="i" aria-hidden="true" focusable="false"><use href="#i-@Name" /></svg>

@code {
    [Parameter, EditorRequired]
    public string Name { get; set; } = "";
}
```

`Components/Ui/IconSprite.razor` (einmal im Layout, Symbole wie im Mockup):

```razor
<svg width="0" height="0" style="position:absolute" aria-hidden="true" focusable="false">
    <defs>
        <symbol id="i-copy" viewBox="0 0 24 24"><rect x="9" y="9" width="12" height="12" rx="2" /><path d="M5 15V5a2 2 0 0 1 2-2h10" /></symbol>
        <symbol id="i-plus" viewBox="0 0 24 24"><path d="M12 5v14M5 12h14" /></symbol>
        <symbol id="i-up" viewBox="0 0 24 24"><path d="m6 15 6-6 6 6" /></symbol>
        <symbol id="i-down" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6" /></symbol>
        <symbol id="i-edit" viewBox="0 0 24 24"><path d="M12 20h9" /><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z" /></symbol>
        <symbol id="i-trash" viewBox="0 0 24 24"><path d="M3 6h18M8 6V4h8v2M6 6l1 14h10l1-14" /></symbol>
        <symbol id="i-back" viewBox="0 0 24 24"><path d="m15 18-6-6 6-6" /></symbol>
        <symbol id="i-chev" viewBox="0 0 24 24"><path d="m9 18 6-6-6-6" /></symbol>
        <symbol id="i-menu" viewBox="0 0 24 24"><path d="M4 7h16M4 12h16M4 17h16" /></symbol>
        <symbol id="i-more" viewBox="0 0 24 24"><circle cx="12" cy="5" r="1" /><circle cx="12" cy="12" r="1" /><circle cx="12" cy="19" r="1" /></symbol>
    </defs>
</svg>
```

`Components/Ui/StatusHex.razor`:

```razor
@using Webionic.ICalMerger.Data

<span class="hex @StatusText.Css(Status)" role="img" aria-label="@Label" title="@Label"></span>

@code {
    [Parameter, EditorRequired]
    public SourceStatus Status { get; set; }

    private string Label => Status switch
    {
        SourceStatus.Ok => "In Ordnung",
        SourceStatus.Stale => "Zwischenspeicher",
        SourceStatus.Error => "Fehler",
        _ => "Noch nicht abgerufen",
    };
}
```

`Components/Ui/ConfirmDialog.razor` (Bestätigung mit Folgetext, Fokus auf „Abbrechen“, Escape bricht ab):

```razor
<div class="scrim" @onclick="Cancel">
    <div class="dialog" role="alertdialog" aria-modal="true" aria-labelledby="dialog-title" aria-describedby="dialog-text"
         tabindex="-1" @onclick:stopPropagation="true" @onkeydown="OnKey">
        <h2 id="dialog-title">@Title</h2>
        <p id="dialog-text">@ChildContent</p>
        <div class="btns">
            <button type="button" class="btn btn-quiet" @ref="cancelButton" @onclick="Cancel" disabled="@Busy">Abbrechen</button>
            <button type="button" class="btn @(Destructive ? "btn-err" : "btn-navy")" @onclick="Confirm" disabled="@Busy">@ConfirmText</button>
        </div>
    </div>
</div>

@code {
    private ElementReference cancelButton;

    [Parameter, EditorRequired] public string Title { get; set; } = "";
    [Parameter, EditorRequired] public string ConfirmText { get; set; } = "";
    [Parameter] public bool Destructive { get; set; } = true;
    [Parameter] public bool Busy { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter, EditorRequired] public EventCallback OnConfirm { get; set; }
    [Parameter, EditorRequired] public EventCallback OnCancel { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) await cancelButton.FocusAsync();
    }

    private Task Cancel() => Busy ? Task.CompletedTask : OnCancel.InvokeAsync();

    private Task Confirm() => OnConfirm.InvokeAsync();

    private Task OnKey(KeyboardEventArgs e) => e.Key == "Escape" ? Cancel() : Task.CompletedTask;
}
```

`Components/Ui/MoreMenu.razor` (Menü ohne JavaScript, schließt nach Klick auf einen Eintrag und bei Klick daneben):

```razor
<div class="menu" @onkeydown="OnKey">
    <button type="button" class="icon-btn" aria-label="@Label" aria-haspopup="menu" aria-expanded="@open" @onclick="Toggle">
        <Icon Name="more" />
    </button>
    @if (open)
    {
        <div class="menu-backdrop" @onclick="Close"></div>
        <div class="menu-list" role="menu" @onclick="Close">
            @ChildContent
        </div>
    }
</div>

@code {
    private bool open;

    [Parameter, EditorRequired] public string Label { get; set; } = "Mehr";
    [Parameter] public RenderFragment? ChildContent { get; set; }

    private void Toggle() => open = !open;

    private void Close() => open = false;

    private void OnKey(KeyboardEventArgs e)
    {
        if (e.Key == "Escape") open = false;
    }
}
```

Ergänze in `Components/_Imports.razor` die Zeile `@using Webionic.ICalMerger.Components.Ui`.

- [ ] **Step 6: Layout mit Topbar**

`Components/Layout/Topbar.razor`:

```razor
@implements IDisposable

@inject NavigationManager Navigation

<header class="topbar">
    <a class="topbar-brand" href=""><img src="img/logo-new-light.svg" alt="Webionic" height="30" /></a>
    <AuthorizeView>
        <Authorized>
            <input id="nav-toggle" class="nav-toggle" type="checkbox" aria-label="Menü öffnen oder schließen" />
            <label for="nav-toggle" class="burger" aria-hidden="true"><Icon Name="menu" /></label>
            <div class="topbar-menu">
                <nav aria-label="Hauptnavigation">
                    <a href="" class="@(OnCalendars ? "active" : null)" aria-current="@(OnCalendars ? "page" : null)">Kalender</a>
                    <AuthorizeView Roles="Admin" Context="admin">
                        <a href="admin/users" class="@(OnAdmin ? "active" : null)" aria-current="@(OnAdmin ? "page" : null)">Nutzer</a>
                    </AuthorizeView>
                </nav>
                <div class="who">
                    <a class="who-name" href="Account/Manage/ChangePassword" title="Passwort ändern">@context.User.Identity?.Name</a>
                    <form action="Account/Logout" method="post">
                        <AntiforgeryToken />
                        <input type="hidden" name="ReturnUrl" value="@currentUrl" />
                        <button type="submit" class="btn btn-quiet btn-sm">Abmelden</button>
                    </form>
                </div>
            </div>
        </Authorized>
    </AuthorizeView>
</header>

@code {
    private string currentUrl = "";

    private bool OnCalendars => currentUrl.Length == 0 || currentUrl.StartsWith("calendars/", StringComparison.OrdinalIgnoreCase);
    private bool OnAdmin => currentUrl.StartsWith("admin/", StringComparison.OrdinalIgnoreCase);

    protected override void OnInitialized()
    {
        currentUrl = Navigation.ToBaseRelativePath(Navigation.Uri);
        Navigation.LocationChanged += OnLocationChanged;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        currentUrl = Navigation.ToBaseRelativePath(e.Location);
        StateHasChanged();
    }

    public void Dispose() => Navigation.LocationChanged -= OnLocationChanged;
}
```

`Components/Layout/MainLayout.razor` ersetzen:

```razor
@inherits LayoutComponentBase

<IconSprite />
<Topbar />

<main class="main">
    @Body
</main>

<div id="blazor-error-ui" data-nosnippet>
    Es ist ein unerwarteter Fehler aufgetreten.
    <a href=".">Neu laden</a>
    <button type="button" class="dismiss">Schließen</button>
</div>
```

In `Components/App.razor` (Stylesheet-Zeile und `lang="de"` stehen seit Task 1, Step 4b): Ergänze direkt vor den Stylesheets zwei Preloads, damit die Schriften beim ersten Rendern bereitstehen:

```razor
<link rel="preload" href="fonts/bricolage-grotesque-latin-wght-normal.woff2" as="font" type="font/woff2" crossorigin />
<link rel="preload" href="fonts/hanken-grotesk-latin-wght-normal.woff2" as="font" type="font/woff2" crossorigin />
```

- [ ] **Step 7: Smoke-Test für das Theme**

`tests/Webionic.ICalMerger.Tests/Ui/ThemeTests.cs`:

```csharp
using System.Net;
using Webionic.ICalMerger.Tests.Support;

namespace Webionic.ICalMerger.Tests.Ui;

public sealed class ThemeTests : IDisposable
{
    private readonly AppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task LoginPage_UsesWebionicThemeAndLogo()
    {
        var response = await _factory.NewClient().GetAsync("/Account/Login");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches(@"css/app(\.[A-Za-z0-9]+)?\.css", html);
        Assert.Contains("img/logo-new-light.svg", html);
        Assert.Contains("lang=\"de\"", html);
    }

    [Fact]
    public async Task Stylesheet_ContainsThemeTokensComponentsAndScannedUtilities()
    {
        var css = await _factory.NewClient().GetStringAsync("/css/app.css");

        Assert.Contains("--color-navy-900", css);   // @theme-Tokens
        Assert.Contains(".btn-primary", css);        // Komponentenklassen
        Assert.Contains(".hex", css);
        Assert.Contains(".max-w-md", css);           // Utility aus dem Markup der .razor-Dateien: @source greift
        Assert.DoesNotContain("--bs-", css);         // kein Bootstrap
    }

    [Theory]
    [InlineData("/css/app.css", "text/css")]
    [InlineData("/fonts/hanken-grotesk-latin-wght-normal.woff2", "font/woff2")]
    [InlineData("/img/logo-new-light.svg", "image/svg+xml")]
    public async Task ThemeAssets_AreServed(string path, string contentType)
    {
        var response = await _factory.NewClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
    }
}
```

- [ ] **Step 8: Build, Tests, Sichtprüfung**

Run: `dotnet build` und danach `dotnet test`
Expected: Build succeeded (der Build erzeugt `wwwroot/css/app.css` neu), alle Tests PASS. Danach zeigt `git status` höchstens eine geänderte `wwwroot/css/app.css`, die mitcommittet wird. Schlägt `ThemeAssets_AreServed` fehl, weil statische Dateien in der Testumgebung nicht gemappt sind, rufe `app.UseStaticFiles()` beziehungsweise `MapStaticAssets()` in `Program.cs` auf (die Vorlage tut das bereits) und prüfe den Content-Root der `AppFactory`, nicht den Test abschwächen.

Starte die App (Befehl aus Task 8, Step 6) und vergleiche `http://localhost:5099/Account/Login` mit `docs/mockups/index.html`: Logo und Schriften (Bricolage für Überschriften, Hanken für Text) laden ohne Anfrage an `fonts.googleapis.com` und ohne Bootstrap-Datei (Netzwerk-Tab), der Button „Anmelden“ ist Amber mit Navy-Text, die Auswahlmarkierung ist hell-amber. In Handybreite (390 px) klappt das Menü der Topbar über das Burger-Symbol auf und zu, ohne JavaScript.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: add Webionic theme, topbar layout and UI building blocks" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Kalender-UI (Übersicht und Detailseite)

Markup, Texte und Zustände folgen dem abgenommenen Mockup `docs/mockups/index.html` (Abschnitte 1 und 2, jeweils Desktop und Handy). Die Komponentenklassen stammen aus `Styles/app.css` (Task 7b), einmalige Details sind Tailwind-Utilities.

**Files:**
- Create: `src/Webionic.ICalMerger/Users/CurrentUser.cs`
- Modify: `src/Webionic.ICalMerger/ServiceRegistration.cs`, `src/Webionic.ICalMerger/Components/_Imports.razor`, `src/Webionic.ICalMerger/Components/Pages/Home.razor`
- Create: `src/Webionic.ICalMerger/Components/Pages/CalendarDetail.razor`
- Test: Build + manueller Test (Blazor-Komponententests sind bewusst nicht Teil des Umfangs; die Logik steckt in getesteten Services und in `StatusText`/`RelativeTime`, Task 7b)

**Interfaces:**
- Consumes: `CalendarService` (alle Methoden aus Task 4), `DomainException`, `AppLimits`, `MergedCalendar`, `CalendarSource`, `SourceStatus`/`GetStatus()` (Task 4), `StatusText`, `RelativeTime`, `Icon`, `StatusHex`, `ConfirmDialog`, `MoreMenu` (Task 7b), `TimeProvider` (Task 5).
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
@using Microsoft.Extensions.Options
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
@inject IOptions<AppLimits> Limits
@inject TimeProvider Time

<PageTitle>Meine Kalender</PageTitle>

<div class="pagehead">
    <div>
        <h1 class="page">Meine Kalender</h1>
        @if (calendars is not null)
        {
            <p class="sub">
                @if (AtLimit)
                {
                    <text>@calendars.Count von @Limits.Value.MaxCalendarsPerUser Kalendern, das Limit ist erreicht</text>
                }
                else
                {
                    <text>@calendars.Count von @Limits.Value.MaxCalendarsPerUser Kalendern</text>
                }
            </p>
        }
    </div>
    <button type="button" class="btn btn-primary" @onclick="StartCreate" disabled="@(calendars is null || AtLimit)">
        <Icon Name="plus" />Neuer Kalender
    </button>
</div>

@if (error is not null)
{
    <div class="notice err" role="alert">@error</div>
}

@if (creating)
{
    <EditForm Model="form" OnValidSubmit="CreateAsync" FormName="create-calendar" class="panel create">
        <label for="new-name" class="field-label">Name des Kalenders</label>
        <div class="inline">
            <InputText @ref="nameInput" id="new-name" @bind-Value="form.Name" class="input" placeholder="z. B. Familie" maxlength="100" autocomplete="off" />
            <button type="submit" class="btn btn-navy" disabled="@busy">Anlegen</button>
            <button type="button" class="btn btn-quiet" @onclick="CancelCreate">Abbrechen</button>
        </div>
    </EditForm>
}

@if (calendars is null)
{
    <p class="text-muted">Lade …</p>
}
else if (calendars.Count == 0)
{
    @if (!creating)
    {
        <p class="text-muted">Noch kein Kalender. Lege deinen ersten mit „Neuer Kalender“ an.</p>
    }
}
else
{
    var now = Time.GetUtcNow().UtcDateTime;
    <div class="cals">
        @foreach (var calendar in calendars)
        {
            var line = StatusText.Calendar(calendar.Sources, now);
            <a class="cal @(line.Status == SourceStatus.Error ? "problem" : null)" href="calendars/@calendar.Id">
                <StatusHex Status="line.Status" />
                <div>
                    <h2>@calendar.Name</h2>
                    <div class="meta">@calendar.Sources.Count @(calendar.Sources.Count == 1 ? "Quelle" : "Quellen")</div>
                </div>
                <div class="state @StatusText.Css(line.Status)">@line.Text</div>
                <span class="chev"><Icon Name="chev" /></span>
            </a>
        }
    </div>
    <ul class="legend" aria-label="Legende">
        <li><span class="hex ok"></span>Alle Quellen erreichbar</li>
        <li><span class="hex warn"></span>Zwischenspeicher, Quelle antwortet nicht</li>
        <li><span class="hex err"></span>Quelle fehlerhaft</li>
    </ul>
}

@code {
    private List<MergedCalendar>? calendars;
    private string ownerId = "";
    private string? error;
    private bool busy;
    private bool creating;
    private bool focusName;
    private InputText? nameInput;
    private readonly NameForm form = new();

    private bool AtLimit => calendars is not null && calendars.Count >= Limits.Value.MaxCalendarsPerUser;

    protected override async Task OnInitializedAsync()
    {
        ownerId = await User.GetIdAsync();
        calendars = await Calendars.ListAsync(ownerId);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (focusName && nameInput?.Element is { } element)
        {
            focusName = false;
            await element.FocusAsync();
        }
    }

    private void StartCreate()
    {
        error = null;
        creating = true;
        focusName = true;
    }

    private void CancelCreate()
    {
        creating = false;
        form.Name = "";
    }

    private async Task CreateAsync()
    {
        error = null;
        busy = true;
        try
        {
            await Calendars.CreateAsync(ownerId, form.Name);
            form.Name = "";
            creating = false;
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
@inject IOptions<AppLimits> Limits
@inject TimeProvider Time

<PageTitle>@(calendar?.Name ?? "Kalender")</PageTitle>

@if (loading)
{
    <p class="text-muted">Lade …</p>
}
else if (calendar is null)
{
    <div class="notice err" role="alert">Kalender nicht gefunden. <a href="">Zur Übersicht</a></div>
}
else
{
    var sources = calendar.Sources;
    var now = Now;
    var atSourceLimit = sources.Count >= Limits.Value.MaxSourcesPerCalendar;

    <a class="back" href=""><Icon Name="back" />Alle Kalender</a>

    <div class="pagehead">
        @if (renaming)
        {
            <form class="inline min-w-0 flex-auto" @onsubmit="RenameAsync">
                <label class="sr-only" for="rename">Name des Kalenders</label>
                <input id="rename" class="input" @bind="renameValue" @bind:event="oninput" maxlength="100" autocomplete="off" />
                <button type="submit" class="btn btn-navy" disabled="@busy">Speichern</button>
                <button type="button" class="btn btn-quiet" @onclick="CancelRename">Abbrechen</button>
            </form>
        }
        else
        {
            <h1 class="page">@calendar.Name</h1>
            <button type="button" class="btn btn-quiet" @onclick="StartRename"><Icon Name="edit" />Umbenennen</button>
        }
    </div>

    @if (error is not null)
    {
        <div class="notice err" role="alert">@error</div>
    }
    @if (info is not null)
    {
        <div class="notice ok" role="status">@info</div>
    }

    <section class="feed" aria-labelledby="feed-title">
        <h2 id="feed-title">Feed-Adresse</h2>
        <p>Diese Adresse in der Kalender-App abonnieren. Wer sie kennt, sieht alle Termine.</p>
        <div class="feedbox">
            <code>@FeedUrl</code>
            <button type="button" class="btn btn-primary" @onclick="CopyAsync"><Icon Name="copy" />@(copied ? "Kopiert" : "Kopieren")</button>
        </div>
        <span class="sr-only" role="status">@(copied ? "Adresse kopiert" : "")</span>
        <div class="tools">
            <span>Zuletzt abgerufen: @RelativeTime.Ago(sources.Select(s => s.LastAttemptAt).Max(), now)</span>
            <button type="button" class="linkbtn" @onclick="() => pending = Pending.Regenerate">Neue Adresse erzeugen</button>
        </div>
    </section>

    <section class="panel" aria-labelledby="sources-title">
        <div class="sec-head">
            <h2 id="sources-title">Quellen</h2>
            <span class="text-[0.8125rem] text-muted">@sources.Count von @Limits.Value.MaxSourcesPerCalendar</span>
        </div>
        <p class="sec-sub">Bei doppelten Terminen gewinnt die Quelle weiter oben.</p>

        @if (sources.Count == 0)
        {
            <p class="text-muted">Noch keine Quellen. Füge unten die erste hinzu.</p>
        }

        @for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var index = i;
            var line = StatusText.Source(source, now);
            var css = StatusText.Css(line.Status);

            <div class="src @(line.Status == SourceStatus.Error ? "bad" : line.Status == SourceStatus.Stale ? "stale" : null)">
                @if (editingId == source.Id)
                {
                    <form class="src-edit" @onsubmit="() => SaveEditAsync(source.Id)">
                        <input class="input" @bind="editName" @bind:event="oninput" placeholder="Name" maxlength="100" aria-label="Name der Quelle" />
                        <input class="input" @bind="editUrl" @bind:event="oninput" placeholder="https://… oder webcal://…" aria-label="Adresse der Quelle" inputmode="url" autocomplete="off" />
                        <div class="btns">
                            <button type="submit" class="btn btn-navy btn-sm" disabled="@busy">@(busy ? "Prüfe Adresse …" : "Speichern")</button>
                            <button type="button" class="btn btn-quiet btn-sm" @onclick="() => editingId = null">Abbrechen</button>
                        </div>
                    </form>
                }
                else
                {
                    <span class="rank" title="Priorität @(index + 1)">@(index + 1)</span>
                    <StatusHex Status="line.Status" />
                    <div>
                        <div class="name">@source.Name</div>
                        <div class="u">@Mask(source.Url)</div>
                        <div class="mst">@line.Text@(line.Detail is null ? "" : $" · {line.Detail}")</div>
                    </div>
                    <div class="st">
                        @line.Text
                        @if (line.Detail is not null)
                        {
                            <br /><span class="detail">@line.Detail</span>
                        }
                    </div>
                    <div class="acts" role="group" aria-label="Aktionen für @source.Name">
                        <button type="button" class="icon-btn" aria-label="@source.Name nach oben" disabled="@(index == 0 || busy)" @onclick="() => MoveAsync(source.Id, -1)"><Icon Name="up" /></button>
                        <button type="button" class="icon-btn" aria-label="@source.Name nach unten" disabled="@(index == sources.Count - 1 || busy)" @onclick="() => MoveAsync(source.Id, 1)"><Icon Name="down" /></button>
                        <button type="button" class="icon-btn" aria-label="@source.Name bearbeiten" @onclick="() => StartEdit(source)"><Icon Name="edit" /></button>
                        <button type="button" class="icon-btn" aria-label="@source.Name entfernen" disabled="@busy" @onclick="() => DeleteSourceAsync(source.Id)"><Icon Name="trash" /></button>
                    </div>
                    <div class="src-more">
                        <MoreMenu Label="Aktionen für @source.Name">
                            <button type="button" role="menuitem" disabled="@(index == 0 || busy)" @onclick="() => MoveAsync(source.Id, -1)">Nach oben</button>
                            <button type="button" role="menuitem" disabled="@(index == sources.Count - 1 || busy)" @onclick="() => MoveAsync(source.Id, 1)">Nach unten</button>
                            <button type="button" role="menuitem" @onclick="() => StartEdit(source)">Bearbeiten</button>
                            <button type="button" role="menuitem" class="danger" disabled="@busy" @onclick="() => DeleteSourceAsync(source.Id)">Entfernen</button>
                        </MoreMenu>
                    </div>
                }
            </div>
        }

        <form class="add" @onsubmit="AddSourceAsync">
            <input class="input" @bind="newName" @bind:event="oninput" placeholder="Name (optional)" maxlength="100" aria-label="Name der neuen Quelle" autocomplete="off" />
            <input class="input" @bind="newUrl" @bind:event="oninput" placeholder="https://… oder webcal://…" aria-label="Adresse der neuen Quelle" inputmode="url" autocomplete="off" />
            <button type="submit" class="btn btn-navy" disabled="@(busy || atSourceLimit)">
                <Icon Name="plus" />@(adding ? "Prüfe Adresse …" : "Quelle hinzufügen")
            </button>
        </form>
        @if (atSourceLimit)
        {
            <p class="text-[0.8125rem] text-muted">Das Limit von @Limits.Value.MaxSourcesPerCalendar Quellen ist erreicht.</p>
        }
    </section>

    <div class="danger-zone">
        <p>Der Kalender und seine Feed-Adresse werden unwiderruflich gelöscht.</p>
        <button type="button" class="btn btn-danger" @onclick="() => pending = Pending.DeleteCalendar">Kalender löschen</button>
    </div>

    @if (pending == Pending.Regenerate)
    {
        <ConfirmDialog Title="Neue Feed-Adresse erzeugen?" ConfirmText="Neue Adresse erzeugen" Busy="busy"
                       OnConfirm="RegenerateAsync" OnCancel="CloseDialog">
            Die bisherige Adresse funktioniert sofort nicht mehr. Alle, die den Kalender abonniert haben, müssen die neue Adresse eintragen.
        </ConfirmDialog>
    }
    else if (pending == Pending.DeleteCalendar)
    {
        <ConfirmDialog Title="@($"Kalender „{calendar.Name}“ löschen?")" ConfirmText="Kalender löschen" Busy="busy"
                       OnConfirm="DeleteCalendarAsync" OnCancel="CloseDialog">
            Der Kalender und seine Feed-Adresse werden unwiderruflich gelöscht. Wer ihn abonniert hat, sieht danach keine Termine mehr.
        </ConfirmDialog>
    }
}

@code {
    private enum Pending { None, Regenerate, DeleteCalendar }

    [Parameter]
    public int Id { get; set; }

    private MergedCalendar? calendar;
    private bool loading = true;
    private bool busy;
    private bool adding;
    private bool copied;
    private string ownerId = "";
    private string? error;
    private string? info;
    private Pending pending;
    private bool renaming;
    private string renameValue = "";
    private int? editingId;
    private string editName = "";
    private string editUrl = "";
    private string newName = "";
    private string newUrl = "";

    private DateTime Now => Time.GetUtcNow().UtcDateTime;

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
            pending = Pending.None;
        }
    }

    private async Task AddSourceAsync()
    {
        adding = true;
        try
        {
            await RunAsync(async () =>
            {
                await Calendars.AddSourceAsync(ownerId, Id, newName, newUrl);
                newName = "";
                newUrl = "";
            }, "Quelle hinzugefügt.");
        }
        finally
        {
            adding = false;
        }
    }

    private void StartEdit(CalendarSource source)
    {
        editingId = source.Id;
        editName = source.Name;
        editUrl = source.Url;
    }

    private Task SaveEditAsync(int sourceId) => RunAsync(async () =>
    {
        await Calendars.UpdateSourceAsync(ownerId, sourceId, editName, editUrl);
        editingId = null;
    }, "Quelle gespeichert.");

    private Task DeleteSourceAsync(int sourceId) => RunAsync(() => Calendars.DeleteSourceAsync(ownerId, sourceId), "Quelle entfernt.");

    private Task MoveAsync(int sourceId, int direction) => RunAsync(() => Calendars.MoveSourceAsync(ownerId, sourceId, direction));

    private void StartRename()
    {
        renameValue = calendar!.Name;
        renaming = true;
    }

    private void CancelRename() => renaming = false;

    private Task RenameAsync() => RunAsync(async () =>
    {
        await Calendars.RenameAsync(ownerId, Id, renameValue);
        renaming = false;
    }, "Name gespeichert.");

    private Task RegenerateAsync() => RunAsync(
        () => Calendars.RegenerateTokenAsync(ownerId, Id),
        "Neue Feed-Adresse erzeugt. Die alte Adresse ist nicht mehr gültig.");

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
            pending = Pending.None;
        }
    }

    private void CloseDialog() => pending = Pending.None;

    private async Task CopyAsync()
    {
        try
        {
            await Js.InvokeVoidAsync("navigator.clipboard.writeText", FeedUrl);
            error = null;
            copied = true;
            StateHasChanged();
            await Task.Delay(2000);
            copied = false;
        }
        catch (JSException)
        {
            error = "Kopieren ist hier nicht möglich. Bitte die Adresse von Hand markieren.";
        }
    }

    private static string Mask(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "…";
        var path = uri.AbsolutePath;
        return $"{uri.Host}{(path.Length > 12 ? path[..12] : path)}…";
    }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: Build succeeded. Behebe Razor-Fehler in den Komponenten (z. B. Lambda-Syntax in `@onclick`, `var` in Markup-Blöcken), nicht durch Entfernen von Funktionen.

- [ ] **Step 5: Gesamtsuite**

Run: `dotnet test`
Expected: alle PASS. `Login_WithBootstrapAdmin_SetsCookieAndOpensHome` ruft `/` auf und muss weiterhin `200` liefern.

- [ ] **Step 6: Manueller Test im Browser**

```bash
ADMIN_EMAIL=admin@example.com ADMIN_PASSWORD='correct horse battery' dotnet run --project src/Webionic.ICalMerger --urls http://localhost:5099
```

Prüfe im Browser unter `http://localhost:5099` (zum Beispiel mit den Chrome-Tools von Claude oder von Hand) und halte `docs/mockups/index.html` daneben:
1. Anmelden mit den obigen Daten, danach Weiterleitung auf „Meine Kalender“. Ohne Kalender steht der Leertext, „Neuer Kalender“ öffnet das Namensfeld mit Fokus.
2. Kalender „Test“ anlegen: Karte mit grauem Sechseck und „Keine Quellen“. Öffnen: Feed-Block im Navy, „Kopieren“ wechselt kurz auf „Kopiert“.
3. Quelle hinzufügen: `webcal://calendar.google.com/calendar/ical/de.austrian%23holiday%40group.v.calendar.google.com/public/basic.ics`. Sie wird akzeptiert und zeigt „Noch nicht abgerufen“ (graues Sechseck), erst der erste Feed-Abruf setzt den Status.
4. Feed-URL mit `curl -s <URL> | head -20`: Der Abruf liefert `BEGIN:VCALENDAR` und Termine. Nach dem Neuladen der Seite ist die Quelle navy mit „vor 1 Min. abgerufen“ und der Kalender in der Übersicht „Alle Quellen erreichbar“.
5. Quelle mit `http://127.0.0.1:8000/x.ics` hinzufügen: Sie wird mit „Adresse nicht erlaubt …“ abgelehnt, die Meldung steht in einem roten Hinweis.
6. Fehlerzustand prüfen: die Adresse einer vorhandenen Quelle nicht erreichbar machen (zum Beispiel lokal einen Server auf dem Port stoppen), 6 Minuten warten (Cache-TTL), Feed erneut abrufen. Die Quelle wird amber (`stale`, „Letzter Erfolg: …“), der Kalender „Zwischenspeicher, Stand: …“. Eine neu hinzugefügte, nie erreichbare Quelle wird rot.
7. „Neue Adresse erzeugen“: Dialog mit Folgetext, Fokus auf „Abbrechen“, Escape schließt. Nach Bestätigung liefert die alte URL `404`.
8. Hoch/Runter ändern die Reihenfolge, die obere Schaltfläche der ersten Quelle ist deaktiviert. „Kalender löschen“ fragt nach und führt zur Übersicht.
9. Fenster auf 390 px Breite: kein horizontales Scrollen, Quellen erscheinen als kompakte Zeilen mit „…“-Menü (Nach oben, Nach unten, Bearbeiten, Entfernen), der Feed-Block stapelt Adresse und „Kopieren“ untereinander.
10. Tastatur: Alle Aktionen sind per Tab erreichbar, der Fokusring ist sichtbar.

Beende den Server danach (Strg+C).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add calendar overview and detail pages" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Admin-Seite für die Nutzerverwaltung

Markup und Texte folgen dem abgenommenen Mockup `docs/mockups/index.html` (Abschnitt 3).

**Files:**
- Create: `src/Webionic.ICalMerger/Components/Pages/AdminUsers.razor`
- Modify: `src/Webionic.ICalMerger/Components/Routes.razor`
- Test: `tests/Webionic.ICalMerger.Tests/Users/AdminPageAccessTests.cs`

**Interfaces:**
- Consumes: `UserAdminService` und `UserRow` mit `CalendarCount` (Task 7), `CurrentUser` (Task 8), `ConfirmDialog`, `MoreMenu`, `Icon` (Task 7b), `Roles.Admin`, `WebHelpers`, `AppFactory`.
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
                    <h1 class="page">Kein Zugriff</h1>
                    <p class="text-muted">Du hast keine Berechtigung für diese Seite.</p>
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

Die Seite folgt dem Mockup (Abschnitt 3): Tabelle mit Rollen- und Statuschips, Kalenderanzahl, einer Hauptaktion pro Zeile und dem Menü „Mehr“, Einladungshinweis in Honig, Bestätigungsdialog zum Löschen. In Handybreite wird jede Zeile zu einem kleinen Block (`data-label` in `Styles/app.css`). „Zuletzt angemeldet“ und „läuft in 7 Tagen ab“ aus dem Mockup entfallen bewusst: Dafür gibt es weder Daten im Modell noch einen Anlass, sie nur für die Anzeige zu speichern.

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

<div class="pagehead">
    <div>
        <h1 class="page">Nutzer</h1>
        @if (users is not null)
        {
            <p class="sub">@users.Count @(users.Count == 1 ? "Konto" : "Konten")</p>
        }
    </div>
    <button type="button" class="btn btn-primary" @onclick="StartInvite"><Icon Name="plus" />Nutzer einladen</button>
</div>

@if (error is not null)
{
    <div class="notice err" role="alert">@error</div>
}
@if (info is not null)
{
    <div class="notice ok" role="status">@info</div>
}

@if (inviting)
{
    <form class="panel create" @onsubmit="InviteAsync">
        <label for="invite-email" class="field-label">E-Mail-Adresse des neuen Nutzers</label>
        <div class="inline">
            <input @ref="inviteInput" id="invite-email" class="input" type="email" @bind="inviteEmail" @bind:event="oninput" placeholder="name@example.com" autocomplete="off" />
            <button type="submit" class="btn btn-navy" disabled="@busy">Einladen</button>
            <button type="button" class="btn btn-quiet" @onclick="CancelInvite">Abbrechen</button>
        </div>
    </form>
}

@if (link is not null)
{
    <div class="invite" role="status">
        <strong>@linkTitle</strong>
        <code>@link</code>
        <button type="button" class="btn btn-navy" @onclick="CopyLinkAsync"><Icon Name="copy" />@(copied ? "Kopiert" : "Link kopieren")</button>
        <button type="button" class="btn btn-quiet" @onclick="() => link = null">Schließen</button>
        <p>Einmaliger Link, 7 Tage gültig. Er wird nur jetzt angezeigt. Gib ihn selbst weiter, es wird keine E-Mail versendet.</p>
    </div>
}

@if (users is null)
{
    <p class="text-muted">Lade …</p>
}
else
{
    <section class="panel table-panel" aria-label="Nutzerliste">
        <table class="data-table">
            <thead>
                <tr>
                    <th scope="col">E-Mail</th>
                    <th scope="col">Status</th>
                    <th scope="col" class="tabular-nums">Kalender</th>
                    <th scope="col"><span class="sr-only">Aktionen</span></th>
                </tr>
            </thead>
            <tbody>
                @foreach (var user in users)
                {
                    var isMe = user.Id == myId;
                    <tr>
                        <td data-label="E-Mail">
                            <strong>@user.Email</strong>
                            @if (isMe)
                            {
                                <div class="text-[0.8125rem] text-muted">Das bist du</div>
                            }
                        </td>
                        <td data-label="Status">
                            <span class="chip @(user.IsAdmin ? "admin" : "user")">@(user.IsAdmin ? "Admin" : "Nutzer")</span>
                            @if (!user.HasPassword)
                            {
                                <span class="chip invited">Eingeladen</span>
                            }
                            @if (user.IsLockedOut)
                            {
                                <span class="chip locked">Gesperrt</span>
                            }
                        </td>
                        <td data-label="Kalender" class="tabular-nums">@user.CalendarCount</td>
                        <td class="actions">
                            @if (user.IsLockedOut)
                            {
                                <button type="button" class="btn btn-quiet btn-sm" disabled="@busy" @onclick="() => SetLockedAsync(user.Id, false)">Entsperren</button>
                            }
                            else
                            {
                                <button type="button" class="btn btn-quiet btn-sm" disabled="@busy" @onclick="() => ResetLinkAsync(user)">@(user.HasPassword ? "Passwort-Link" : "Neuer Link")</button>
                            }
                            <MoreMenu Label="@($"Weitere Aktionen für {user.Email}")">
                                <button type="button" role="menuitem" disabled="@busy" @onclick="() => SetAdminAsync(user.Id, !user.IsAdmin)">@(user.IsAdmin ? "Admin entziehen" : "Zum Admin machen")</button>
                                @if (!user.IsLockedOut)
                                {
                                    <button type="button" role="menuitem" disabled="@(busy || isMe)" @onclick="() => SetLockedAsync(user.Id, true)">Sperren</button>
                                }
                                <button type="button" role="menuitem" class="danger" disabled="@(busy || isMe)" @onclick="() => deleteTarget = user">Löschen</button>
                            </MoreMenu>
                        </td>
                    </tr>
                }
            </tbody>
        </table>
    </section>
    <p class="mt-5 text-[0.8125rem] text-muted">Der letzte Admin kann nicht gesperrt, herabgestuft oder gelöscht werden.</p>
}

@if (deleteTarget is not null)
{
    <ConfirmDialog Title="@($"Nutzer {deleteTarget.Email} löschen?")" ConfirmText="Nutzer löschen" Busy="busy"
                   OnConfirm="DeleteAsync" OnCancel="() => deleteTarget = null">
        Alle Kalender dieses Nutzers werden mitgelöscht, ihre Feed-Adressen funktionieren danach nicht mehr.
    </ConfirmDialog>
}

@code {
    private List<UserRow>? users;
    private string myId = "";
    private string inviteEmail = "";
    private bool inviting;
    private bool focusInvite;
    private ElementReference inviteInput;
    private UserRow? deleteTarget;
    private string? link;
    private string linkTitle = "";
    private bool copied;
    private string? error;
    private string? info;
    private bool busy;

    private string BaseUri => Navigation.BaseUri;

    protected override async Task OnInitializedAsync()
    {
        myId = await User.GetIdAsync();
        users = await Admin.ListAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (focusInvite)
        {
            focusInvite = false;
            await inviteInput.FocusAsync();
        }
    }

    private void StartInvite()
    {
        inviting = true;
        focusInvite = true;
    }

    private void CancelInvite()
    {
        inviting = false;
        inviteEmail = "";
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
            deleteTarget = null;
        }
    }

    private Task InviteAsync() => RunAsync(async () =>
    {
        link = await Admin.InviteAsync(inviteEmail, BaseUri);
        linkTitle = $"Einladung für {inviteEmail.Trim()} erstellt.";
        inviteEmail = "";
        inviting = false;
        copied = false;
    });

    private Task ResetLinkAsync(UserRow user) => RunAsync(async () =>
    {
        link = await Admin.CreateResetLinkAsync(user.Id, BaseUri);
        linkTitle = user.HasPassword ? $"Passwort-Link für {user.Email} erstellt." : $"Neuer Einladungslink für {user.Email} erstellt.";
        copied = false;
    });

    private Task SetLockedAsync(string id, bool locked) =>
        RunAsync(() => Admin.SetLockedAsync(id, locked), locked ? "Nutzer gesperrt." : "Nutzer entsperrt.");

    private Task SetAdminAsync(string id, bool isAdmin) =>
        RunAsync(() => Admin.SetAdminAsync(id, isAdmin), isAdmin ? "Nutzer ist jetzt Admin." : "Admin-Rolle entzogen.");

    private Task DeleteAsync() => RunAsync(
        () => Admin.DeleteAsync(deleteTarget!.Id, myId),
        "Nutzer gelöscht.");

    private async Task CopyLinkAsync()
    {
        try
        {
            await Js.InvokeVoidAsync("navigator.clipboard.writeText", link);
            copied = true;
            StateHasChanged();
            await Task.Delay(2000);
            copied = false;
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

Starte die App wie in Task 8 und prüfe als Admin, mit `docs/mockups/index.html` (Abschnitt 3) als Vorlage:
1. Nutzer „familie@example.com“ einladen. Der Hinweis in Honig zeigt den Link mit dem Satz „Er wird nur jetzt angezeigt“, in der Tabelle stehen die Chips „Nutzer“ und „Eingeladen“, in der Spalte „Kalender“ steht 0.
2. Link in einem privaten Fenster öffnen, E-Mail und Passwort setzen, danach anmelden und einen eigenen Kalender anlegen. Zurück beim Admin zeigt die Tabelle 1 Kalender und den Chip „Eingeladen“ nicht mehr.
3. Über „Mehr“ sperren (Chip „Gesperrt“, Hauptaktion wird „Entsperren“; der Nutzer wird nach spätestens 1 Minute ausgeloggt und kann sich nicht mehr anmelden), entsperren, zum Admin machen und Admin entziehen, danach löschen. Der Dialog nennt, dass die Kalender des Nutzers mitgelöscht werden; Escape schließt ihn.
4. Den einzigen Admin zu sperren, herabzustufen oder zu löschen scheitert mit der deutschen Fehlermeldung; „Sperren“ und „Löschen“ der eigenen Zeile sind deaktiviert.
5. In Handybreite (390 px) wird jede Zeile ein Block mit „E-Mail: …“, „Status: …“, „Kalender: …“ und den Aktionen darunter, ohne horizontales Scrollen.

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
**/tools
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
# publish führt das Build-Target aus der .csproj aus: Es lädt die gepinnte Tailwind-CLI (Netzwerk im Build-Stage nötig)
# und erzeugt wwwroot/css/app.css neu. Node wird nicht gebraucht, die CLI landet nicht im Runtime-Image.
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

### CSS (Tailwind, kein Node nötig)

Das Styling ist Tailwind CSS v4 über die Standalone-CLI. Eingabe ist `src/Webionic.ICalMerger/Styles/app.css`
(Tokens, Komponentenklassen), Ausgabe `src/Webionic.ICalMerger/wwwroot/css/app.css` (erzeugt und eingecheckt).
`dotnet build` lädt die CLI beim ersten Mal nach `src/Webionic.ICalMerger/tools/tailwind/` (gitignored) und erzeugt
die CSS-Datei bei jedem Build neu. Beim Entwickeln zwei Terminals nutzen:

```bash
# Terminal 1: CSS im Watch-Modus neu erzeugen
dotnet msbuild src/Webionic.ICalMerger -t:TailwindWatch
# Terminal 2: App mit Hot Reload, ohne dass jeder Build das CSS erneut erzeugt
dotnet watch run --project src/Webionic.ICalMerger -p:SkipTailwind=true
```

Vor dem Commit einmal `dotnet build` ausführen, damit die minimierte `wwwroot/css/app.css` committet wird.
Utility-Klassen müssen im Markup ausgeschrieben stehen (`class="mt-5"`, nicht `class="mt-@n"`), sonst findet
der Scanner sie nicht.
```

- [ ] **Step 5: Gesamtprüfung**

```bash
dotnet build -c Release
dotnet test
git status --short src/Webionic.ICalMerger/wwwroot/css/app.css
```

Expected: Build succeeded, alle Tests PASS. Zeigt `git status` eine geänderte `wwwroot/css/app.css`, ist die eingecheckte CSS-Datei veraltet: mit dem nächsten Commit mit einchecken.

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
- UI auf Deutsch, URLs gekürzt, Kopieren-Button, Bestätigungen: Task 1, 8, 9, 10. Webionic-Theme, Layout, Statusstufen und UI-Bausteine laut Mockup `docs/mockups/index.html`: Task 4 (`SourceStatus`), 7b, 8, 9.
- Forwarded Headers, Volume `/data`, Data-Protection-Keys, Auto-Migration: Task 1, 10.

**Abweichungen von der Spec (bewusst, im Plan festgehalten):**
- Der Connection-String heißt `DefaultConnection` (Vorlagenname) statt `Default`.
- Der Link nutzt die Vorlagenseite `/Account/ResetPassword?code=…` statt einer neuen Seite `/Account/SetPassword?userId=…&token=…`. Die Seite fragt zur Bestätigung die E-Mail-Adresse ab.
- Zusätzlich: Fehler-Backoff von 1 Minute pro Quelle und Sperrung nach 5 Fehlversuchen beim Login (Identity-Lockout), damit weder ausgefallene Quellen noch Passwort-Raten den Dienst belasten.
- Styling mit Tailwind CSS v4 (Standalone-CLI) statt Bootstrap. Die Spec nannte noch „Bootstrap aus der Vorlage“; die Vorlage wird entsprechend entkernt (Task 1, Step 4b). Die erzeugte `wwwroot/css/app.css` wird eingecheckt.
- Aus dem Mockup entfallen „Zuletzt angemeldet“ und „läuft in 7 Tagen ab“ (keine Daten im Modell), und die Statusstufe „Zwischenspeicher“ wird aus `LastError` plus früherem `LastSuccessAt` abgeleitet (ein Fehler mit früherem Erfolg ist amber statt rot; nie erfolgreiche Quellen sind rot). Der Fokusring ist Navy statt Amber (Kontrast).

**Typkonsistenz:** `CalendarService`-Signaturen (Task 4) stimmen mit den Aufrufen in Task 6 (`RegenerateTokenAsync`), Task 8 (UI) und Tests überein. `FeedService.BuildAsync` → `FeedResult?` (Task 5) wird in Task 6 so verwendet. `UserAdminService`-Signaturen (Task 7) stimmen mit Task 9 überein. `WebHelpers`/`AppFactory` aus Task 6 und 7 werden in Task 9 wiederverwendet.

**Review Focus:** Jede der fünf Zeilen hat Tests: BOM/LF/ATTACH und abgeschnittene Termine sowie Namens-Escaping (Task 2), HTML-Antwort (Task 4 und Task 5), Müll-Token (Task 6).
