# iCal Merger

Fasst mehrere iCal-URLs zu einer Feed-URL zusammen, zum Beispiel für den Familienkalender.
Mehrere Nutzer, jeder mit eigenen Kalendern. Neue Nutzer lädt der Admin per Link ein (kein E-Mail-Versand nötig).

## So funktioniert es

1. Anmelden und einen Kalender anlegen.
2. iCal-URLs (`https://…` oder `webcal://…`) als Quellen hinzufügen.
3. Die angezeigte Feed-URL (`https://<host>/feed/<token>.ics`) in Apple Kalender, Google Kalender oder Outlook abonnieren und an die Familie weitergeben.

Wer die Feed-URL kennt, kann den Kalender lesen. Mit „Neue Adresse erzeugen“ wird die alte URL ungültig.
Quellen werden live abgerufen (5 Minuten Cache). Fällt eine Quelle aus, liefert der Feed die übrigen Quellen
und zeigt bei Bedarf den letzten bekannten Stand der ausgefallenen.

Hinweis: Quell-URLs, die auf interne Adressen zeigen (localhost, 10.x, 192.168.x, 172.16–31.x, Link-Local),
werden aus Sicherheitsgründen abgelehnt.

## Betrieb auf Dokploy

1. Neue Anwendung aus diesem Repository anlegen, Build-Typ **Dockerfile**, Port **8080**.
   Der Build-Stage lädt beim Publish die Tailwind-CLI herunter und braucht dafür Netzwerkzugang.
2. Umgebungsvariablen setzen:
   - `ADMIN_EMAIL` und `ADMIN_PASSWORD`: legen den ersten Admin an (Passwort mindestens 10 Zeichen), aber **nur, solange die Datenbank noch gar keine Nutzer enthält**. Nach dem ersten Start `ADMIN_PASSWORD` wieder aus der Umgebung entfernen.
   - `App__PublicBaseUrl`: öffentliche Basis-URL, zum Beispiel `https://kalender.example.org`. Daraus werden Einladungs- und Reset-Links gebaut. In Produktion unbedingt setzen, sonst wird der Host der jeweiligen Anfrage verwendet.
3. Volume auf `/data` mounten. Es enthält die SQLite-Datenbank (`/data/app.db`) und die Data-Protection-Schlüssel (`/data/keys`).
   Das Dockerfile setzt `DataProtection__KeysPath=/data/keys` bereits. Liegen die Schlüssel nicht auf dem Volume, werden bei jedem Redeploy alle Einladungs-/Reset-Links und Login-Cookies ungültig.
   Das Verzeichnis muss für den Container-Nutzer (Nicht-Root, `$APP_UID`) beschreibbar sein. Ein benanntes Docker-Volume übernimmt die Rechte aus dem Image; bei einem Bind-Mount muss der Host-Ordner entsprechend freigegeben werden.
4. Domain mit HTTPS zuweisen. Die App wertet die `X-Forwarded-*`-Header des Proxys aus.

Optionale Einstellungen:

| Variable | Standard | Bedeutung |
|---|---|---|
| `App__PublicBaseUrl` | leer (Host der Anfrage) | Basis-URL für Einladungs- und Reset-Links |
| `ConnectionStrings__DefaultConnection` | `Data Source=/data/app.db;Default Timeout=30` | Datenbank |
| `DataProtection__KeysPath` | `/data/keys` | Ablage der Schlüssel (muss auf dem Volume liegen) |
| `Limits__MaxCalendarsPerUser` | `10` | Kalender pro Nutzer |
| `Limits__MaxSourcesPerCalendar` | `20` | Quellen pro Kalender |

### Sicherheitshinweise für den Betrieb

- **Container-Port nur über den Proxy erreichbar machen.** Die App vertraut `X-Forwarded-*` von jedem Absender (`KnownProxies` ist geleert). In Dokploy deshalb keinen Host-Port veröffentlichen und Port 8080 nie direkt ins Netz stellen, sonst kann jeder Client Host, Schema und IP fälschen.
- **Feed-Tokens sind Geheimnisse im URL-Pfad.** `Logging__LogLevel__Microsoft.AspNetCore` mindestens auf `Warning` lassen (so ist die App konfiguriert) und nicht auf `Information` anheben, sonst landen Feed-URLs im App-Log. Auch die Zugriffslogs von Traefik/Dokploy enthalten die Feed-URL: nicht an andere Systeme weiterleiten oder für `/feed` deaktivieren.
- **Backups:** SQLite-Datenbank und Schlüssel liegen beide auf dem Volume `/data`. Immer das ganze Volume sichern. Die SQLite-Datenbank läuft im WAL-Modus: ein Backup im laufenden Betrieb mit `sqlite3 /data/app.db ".backup ziel.db"` erstellen oder vorher den Container stoppen, nicht einfach die Datei kopieren.
- **Notfall:** Sind alle Admins ausgesperrt (gesperrt oder Passwort verloren), lässt sich das nur von Hand beheben: die Datenbank `/data/app.db` mit `sqlite3` korrigieren (z. B. `LockoutEnd` leeren oder eine Admin-Rolle zuweisen) oder nur `/data/app.db` (samt `-wal`/`-shm`) löschen, damit `ADMIN_EMAIL`/`ADMIN_PASSWORD` beim nächsten Start einen neuen Admin anlegen. Die Schlüssel in `/data/keys` bleiben dabei erhalten, das ganze Volume muss dafür nicht zurückgesetzt werden.
- **Letzter Admin:** Der letzte aktive Admin kann nicht gelöscht, gesperrt oder herabgestuft werden. Werden zwei Admins gleichzeitig herabgestuft, kann diese Prüfung theoretisch umgangen werden (Race). Bei Bedarf im Notfall wie oben vorgehen.

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

## Schriften

Bricolage Grotesque und Hanken Grotesk sind selbst gehostet (`src/Webionic.ICalMerger/wwwroot/fonts/`) und stehen unter der
SIL Open Font License 1.1. Lizenztext und Copyright-Zeilen: `src/Webionic.ICalMerger/wwwroot/fonts/OFL.txt`.
