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
