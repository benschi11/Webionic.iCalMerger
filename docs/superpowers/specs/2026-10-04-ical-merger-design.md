# iCal Merger – Design

Datum: 2026-10-04

## Ziel

Eine kleine Blazor-App, mit der angemeldete Nutzer mehrere iCal-URLs zu einem
Merge-Kalender zusammenfassen. Jeder Merge-Kalender bekommt eine eigene,
öffentlich abrufbare, nicht erratbare Feed-URL, die z. B. an Familienmitglieder
weitergegeben und in Apple/Google/Outlook-Kalender abonniert wird.

## Festgelegte Anforderungen

- Blazor Web App (Interactive Server), aktuelles .NET-LTS (.NET 10).
- Deployment auf Dokploy als Docker-Container, Daten auf persistentem Volume.
- ASP.NET Core Identity mit Nutzerverwaltung, mandantenfähig:
  jeder Nutzer sieht und verwaltet nur seine eigenen Merge-Kalender.
- Keine Selbstregistrierung. Ein Admin legt Nutzer an.
- Einladung und Passwort-Reset per einmaligem Link, den der Admin selbst
  weitergibt. Kein SMTP.
- Merge ist reines Zusammenführen mit Duplikat-Entfernung. Keine Präfixe,
  kein „Belegt“-Modus (YAGNI, später nachrüstbar).
- Feed-URLs benötigen keinen Login (Kalender-Apps können sich nicht anmelden),
  sondern sind durch ein langes Zufallstoken geschützt.

## Nicht-Ziele

- E-Mail-Versand, Selbstregistrierung, externe Logins (Google/Microsoft).
- Bearbeiten von Terminen, Verarbeiten von `VTODO`/`VJOURNAL`/`VFREEBUSY`
  (nur `VEVENT` wird übernommen).
- Persistente Speicherung der Kalenderinhalte (Quellen werden live geholt).
- Pro-Quelle-Präfixe, Farben, Privatsphäre-Filter.

## Architektur

Eine ASP.NET-Core-App, zwei Solution-Projekte:

```
Webionic.iCalMerger.sln
src/Webionic.ICalMerger/          Blazor Web App (UI, Identity, Feed-Endpunkt)
tests/Webionic.ICalMerger.Tests/  xUnit
```

Einheiten (jede einzeln testbar, klare Schnittstelle):

| Einheit | Aufgabe | Abhängigkeiten |
|---|---|---|
| `IcsMerger` | Reine Funktion: Liste von ICS-Texten + Kalendername → ein ICS-Text | keine |
| `SafeHttpFetcher` | Holt eine URL sicher (Timeout, Größenlimit, SSRF-Schutz) | `HttpClient` |
| `SourceCache` | Cache pro Quell-URL, 5 Min TTL, behält letzten guten Stand | eigener `ConcurrentDictionary`-Cache mit Idle-Eviction (7 Tage) und Single-Flight pro URL, Fetcher |
| `FeedService` | Lädt Quellen eines Merge-Kalenders parallel, ruft `IcsMerger` | Cache, DB |
| `GET /feed/{token}.ics` | Öffentlicher Minimal-API-Endpunkt | `FeedService` |
| Blazor-Seiten | Login, Kalenderliste, Kalenderdetail, Admin-Nutzerverwaltung | Identity, EF Core |

## Datenmodell (EF Core, SQLite)

Identity-Tabellen (`ApplicationUser : IdentityUser`, Rolle `Admin`) plus:

- `MergedCalendar`: `Id`, `OwnerId` (FK User, Cascade Delete), `Name`,
  `Token` (unique, 32 Zufallsbytes, base64url), `CreatedAt`.
- `CalendarSource`: `Id`, `MergedCalendarId` (FK, Cascade Delete), `Name`,
  `Url`, `SortOrder`, `LastAttemptAt`, `LastSuccessAt`, `LastError`.

Limits (konfigurierbar, Defaults): 10 Merge-Kalender pro Nutzer,
20 Quellen pro Kalender.

Quell-URLs gelten als geheim (z. B. Googles „geheime Adresse“): Sie werden nie
geloggt und in der UI standardmäßig gekürzt angezeigt (Host + Anfang).

## Merge-Logik (`IcsMerger`, textbasiert)

1. Jede Quelle: Zeilenumbrüche normalisieren, gefaltete Zeilen entfalten
   (RFC 5545 §3.1).
2. Blöcke `BEGIN:VEVENT … END:VEVENT` und `BEGIN:VTIMEZONE … END:VTIMEZONE`
   extrahieren. Alles andere verwerfen. Der Blockinhalt bleibt unverändert.
3. Duplikate: Schlüssel `UID` + `RECURRENCE-ID` (leer, wenn nicht vorhanden).
   Bei Konflikt gewinnt die Quelle mit der kleineren `SortOrder`.
   Events ohne `UID` werden immer übernommen.
4. `VTIMEZONE` wird pro `TZID` nur einmal ausgegeben (erste Quelle gewinnt).
5. Ausgabe, mit CRLF und Faltung bei 75 Oktetten:
   ```
   BEGIN:VCALENDAR
   VERSION:2.0
   PRODID:-//Webionic//iCalMerger//EN
   CALSCALE:GREGORIAN
   X-WR-CALNAME:<Kalendername>
   REFRESH-INTERVAL;VALUE=DURATION:PT1H
   X-PUBLISHED-TTL:PT1H
   <VTIMEZONEs>
   <VEVENTs>
   END:VCALENDAR
   ```
6. Eine Quelle ohne `BEGIN:VCALENDAR` gilt als ungültig (Fehler, siehe unten).

## Abruf und Cache

- `SafeHttpFetcher`: nur `http`/`https` (`webcal://` wird zu `https://`
  umgeschrieben), Timeout 10 s, max. 10 MB, max. 5 Redirects.
- SSRF-Schutz: Die Ziel-IP wird beim Verbindungsaufbau geprüft
  (`SocketsHttpHandler.ConnectCallback`), nicht nur vorab. Damit sind auch
  Redirects und DNS-Rebinding abgedeckt. Geblockt: Loopback, Private
  (RFC 1918), Link-Local, Unique-Local (IPv6), Multicast, unspezifiziert.
- `SourceCache`: Erfolgreiche Antworten 5 Minuten im Speicher. Schlägt ein
  Abruf fehl, wird der letzte gute Stand weiterverwendet (Stale-on-Error).
  Gibt es keinen, wird die Quelle für diesen Abruf ausgelassen. Nach einem
  Neustart ist der Cache leer.
- Gleichzeitige Abrufe derselben URL werden zusammengefasst (ein Abruf pro URL
  gleichzeitig).
- Nach einem fehlgeschlagenen Abruf wird dieselbe URL 1 Minute lang nicht erneut
  abgerufen (Backoff), damit eine ausgefallene Quelle Feed-Abrufe nicht ausbremst.
- Der Status (`LastAttemptAt`, `LastSuccessAt`, `LastError`) wird nach jedem
  Abruf in `CalendarSource` aktualisiert und in der UI angezeigt.

## Feed-Endpunkt

`GET /feed/{token}.ics`, anonym.

- Unbekanntes Token: `404` ohne Details.
- Erfolg: `200`, `Content-Type: text/calendar; charset=utf-8`,
  `Cache-Control: public, max-age=300`.
- Fallen alle Quellen aus und gibt es keinen Cache: `200` mit leerem, gültigem
  Kalender wäre irreführend (Apps würden alle Termine löschen), daher `503`.
  Fällt nur ein Teil aus, wird der Rest geliefert.
- Das Token lässt sich pro Kalender neu erzeugen. Die alte URL wird sofort
  ungültig.

## Nutzerverwaltung (Identity)

- Selbstregistrierung ist deaktiviert (Register-Seiten entfernt). Externe
  Logins sind aus.
- Bootstrap: Existiert kein Nutzer, wird beim Start ein Admin aus den
  Umgebungsvariablen `ADMIN_EMAIL` und `ADMIN_PASSWORD` angelegt. Fehlen sie
  und es gibt keine Nutzer, bricht der Start mit klarer Fehlermeldung ab.
- Admin-Seite `/admin/users` (Rolle `Admin`): Nutzer anlegen (E-Mail, ohne
  Passwort), Einladungslink erzeugen, Passwort-Reset-Link erzeugen, Nutzer
  sperren/entsperren (Lockout), Nutzer löschen (mit Bestätigung), Admin-Rolle
  vergeben oder entziehen.
- Link: `/Account/ResetPassword?code=…` (Vorlagenseite von Identity, `code` ist das
  Base64Url-kodierte Token aus `GeneratePasswordResetTokenAsync`). Der Nutzer gibt
  zur Bestätigung seine E-Mail-Adresse ein und wählt ein Passwort (mindestens 10 Zeichen). Der Admin sieht den Link einmal und
  kopiert ihn. Gültigkeit 7 Tage (`DataProtectionTokenProviderOptions`).
  Ein Token ist nach Benutzung ungültig, weil sich der Security-Stamp ändert.
- Schutz vor Aussperren: Der letzte Admin kann sich weder löschen noch sperren
  noch die Rolle entziehen. Ein Admin kann sich nicht selbst löschen.
- Eingeloggte Nutzer können ihr Passwort ändern (`/Account/Manage/ChangePassword`).
- Nach 5 falschen Anmeldeversuchen wird das Konto 15 Minuten gesperrt (Identity-Lockout).
- Autorisierung: Jede Abfrage und Mutation von Kalendern/Quellen filtert auf
  `OwnerId == aktueller Nutzer`. Fremde IDs ergeben „nicht gefunden“.

## UI

- `/` Liste der eigenen Merge-Kalender (Name, Anzahl Quellen, Status-Indikator),
  Button „Neuer Kalender“.
- `/calendars/{id}`: Name bearbeiten. Quellen hinzufügen, bearbeiten, löschen
  und sortieren (Name + URL). Pro Quelle Status (zuletzt erfolgreich, Fehlertext).
  Feed-URL mit „Kopieren“-Button, „Token neu erzeugen“ mit Bestätigung,
  „Kalender löschen“ mit Bestätigung.
- Beim Hinzufügen einer Quelle wird die URL einmal probeweise geholt. Ungültige
  oder nicht erreichbare URLs werden mit Fehlermeldung abgelehnt.
- Die Feed-URL wird aus dem Request-Host gebildet (`https://<host>/feed/<token>.ics`).
  Hinter dem Dokploy-Proxy müssen dafür `X-Forwarded-*`-Header ausgewertet
  werden (`ForwardedHeadersOptions`).
- Status je Quelle in vier Stufen, abgeleitet aus `LastSuccessAt`/`LastError`: in Ordnung, Zwischenspeicher
  (Fehler, aber früher erfolgreich: der Feed liefert die letzten Daten), Fehler (nie erfolgreich), noch nicht
  abgerufen. Die Kalenderliste zeigt den schlechtesten Status der Quellen. Fehler sind rot, nicht dekorativ.
- Bestätigungsdialoge mit Folgetext für „Neue Feed-Adresse erzeugen“, „Kalender löschen“ und „Nutzer löschen“.
- Admin-Seite mit Nutzertabelle (Rolle, Eingeladen/Gesperrt, Anzahl Kalender), Einladungslink in einem Hinweis,
  der nur einmal angezeigt wird.
- Texte auf Deutsch. Webionic-Theme (Navy, Honig-Amber, Bricolage Grotesque/Hanken Grotesk, Sechseck als
  Statuszeichen) mit Tailwind CSS v4 (Standalone-CLI, kein Node, kein Bootstrap): Tokens und Komponentenklassen in
  `Styles/app.css`, erzeugte Ausgabe `wwwroot/css/app.css` (eingecheckt). Handybreite ist
  vollwertig unterstützt (Quellen als kompakte Zeilen, Nutzerliste als Blöcke). Vorlage: `docs/mockups/index.html`.

## Fehlerbehandlung

- Quelle nicht erreichbar, Timeout, zu groß, ungültiges ICS, SSRF-Block:
  wird pro Quelle als `LastError` festgehalten, der Feed liefert die übrigen
  Quellen weiter.
- Fehler in einer Quelle verhindern nie den gesamten Feed, außer alle fallen aus
  (siehe `503`).
- Unerwartete Ausnahmen werden geloggt, ohne Quell-URLs im Log.

## Deployment

- Mehrstufiges `Dockerfile` (SDK-Build, ASP.NET-Runtime), läuft als Nicht-Root.
- Volume `/data` mit SQLite-Datei und Data-Protection-Keys (Logins und
  Reset-Links überleben Neustarts).
- Konfiguration über Umgebungsvariablen: `ConnectionStrings__DefaultConnection`
  (Default `Data Source=/data/app.db`), `ADMIN_EMAIL`, `ADMIN_PASSWORD`,
  optional die Limits.
- Migrationen laufen beim Start automatisch.
- HTTPS terminiert Dokploy/Traefik, die App vertraut den Forwarded-Headern.

## Tests

- `IcsMerger` (Unit): Line Unfolding/Folding, Dedupe nach UID und
  RECURRENCE-ID, Events ohne UID, `VTIMEZONE`-Deduplizierung, Rangfolge nach
  `SortOrder`, Verwerfen von `VTODO`, Unicode und lange Zeilen, ungültige Eingabe.
- `SafeHttpFetcher` (Unit/Integration): geblockte IP-Bereiche, Redirect auf
  interne Adresse, Größenlimit, Timeout, `webcal://`-Umschreibung.
- `SourceCache`: TTL, Stale-on-Error, Zusammenfassen gleichzeitiger Abrufe.
- Feed-Endpunkt (Integration mit `WebApplicationFactory`, Fake-Fetcher):
  `404` bei unbekanntem Token, `200` mit korrektem Inhalt und Headern,
  Teilausfall, Totalausfall (`503`), altes Token nach Neuerzeugung.
- Autorisierung: Nutzer A erreicht Kalender von Nutzer B nicht.
- Nutzerverwaltung: Einladungslink setzt Passwort, Link nur einmal gültig,
  letzter Admin nicht entfernbar, gesperrter Nutzer kann sich nicht anmelden,
  Selbstregistrierung nicht erreichbar.

## Offene Punkte

Keine. Spätere Erweiterungen (nicht Teil dieses Umfangs): Präfix pro Quelle,
„Belegt“-Modus, persistenter Cache, SMTP-Einladungen.
