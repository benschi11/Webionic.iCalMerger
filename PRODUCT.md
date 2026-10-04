# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

Blazor Web App (Interactive Server, .NET 10) mit ASP.NET Core Identity, SQLite, Docker auf Dokploy. Entschieden im Design-Spec `docs/superpowers/specs/2026-10-04-ical-merger-design.md`.

## Users

Hauptsächlich der Betreiber selbst (Admin), der am Desktop und gelegentlich am Handy seine Kalender und Quellen pflegt. Die Familie sieht die Oberfläche in der Regel nicht: Sie abonniert nur die Feed-URL in ihrer Kalender-App (Apple, Google, Outlook). Weitere Nutzer können vom Admin eingeladen werden und verwalten dann eigene Kalender, die andere nicht sehen.

## Product Purpose

Mehrere iCal-URLs zu einem Merge-Kalender zusammenfassen und dafür eine eigene, nicht erratbare Feed-URL bereitstellen, die man der Familie gibt. Erfolg: Die Familie sieht alle Termine in einem Kalender, und der Betreiber erkennt auf einen Blick, ob alle Quellen funktionieren.

## Positioning

Kleines, selbst betriebenes Werkzeug für den Heimgebrauch. Kein SaaS, kein Konto bei Dritten, kein E-Mail-Versand. Einladungen und Passwort-Resets laufen über Links, die der Admin selbst weitergibt.

## Operating Context

Läuft als Docker-Container auf Dokploy hinter HTTPS-Proxy. Quellen werden live abgerufen (5 Minuten Cache). Feed-URLs sind öffentlich abrufbar, aber durch ein langes Zufallstoken geschützt. Die Verwaltungsoberfläche ist nur nach Login erreichbar.

## Capabilities and Constraints

- Merge-Kalender mit Quellenliste (Name, URL, Reihenfolge = Priorität bei Duplikaten, Status je Quelle).
- Feed-URL kopieren, Token neu erzeugen (alte URL wird sofort ungültig).
- Admin: Nutzer einladen (einmaliger Link, 7 Tage gültig), Reset-Link, sperren, Admin-Rolle, löschen.
- Limits: 10 Kalender pro Nutzer, 20 Quellen pro Kalender.
- Quell-URLs gelten als geheim und werden gekürzt angezeigt.
- Alle sichtbaren Texte auf Deutsch.
- Nicht vorgesehen: Selbstregistrierung, SMTP, Bearbeiten von Terminen, Präfixe oder Filter pro Quelle.

## Brand Commitments

Webionic-Branding mit dem neuen Logo (`Webionic.Website/src/OrchardCore.Webionic.Theme/wwwroot/img/logo-new-dark.svg` und `logo-new-light.svg`). Das visuelle System liegt in `Webionic.Website/DESIGN.md` (Navy und Honig-Amber, Bricolage Grotesque und Hanken Grotesk, Sechseck als Leitform). Verbindlich nach Aussage des Nutzers.

## Evidence on Hand

Keine echten Nutzerdaten, Screenshots oder Kennzahlen. Beispieldaten in Mockups sind synthetisch und als solche zu kennzeichnen.

## Product Principles

- Der Zustand der Quellen ist wichtiger als jede Dekoration: Fehler sind sofort sichtbar.
- Die Feed-URL ist das Ergebnis und steht dort, wo man sie braucht.
- Gefährliche Aktionen (Token neu erzeugen, löschen) sind klar benannt und werden bestätigt.
- Ein Werkzeug für den Alltag zu Hause: ruhig, schnell erfassbar, ohne Erklärtexte, die niemand liest.

## Accessibility & Inclusion

Keine produktspezifischen Vorgaben bestätigt. Bedienbar per Tastatur, ausreichende Kontraste, nutzbar in Handybreite.
