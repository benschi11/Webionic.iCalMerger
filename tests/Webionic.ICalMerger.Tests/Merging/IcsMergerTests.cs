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
    public void Merge_KeepsEventsWithEmptyUid()
    {
        var first = Calendar(Event("", "Eins"));
        var second = Calendar(Event("", "Zwei"));

        var result = IcsMerger.Merge("K", [first, second]);

        Assert.Contains("SUMMARY:Eins", result);
        Assert.Contains("SUMMARY:Zwei", result);
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
        var source = "﻿" + Calendar(Event("bom@x")).Replace("\r\n", "\n");

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
    public void Merge_TruncatedTimezoneDoesNotSwallowFollowingEvents()
    {
        var truncatedTz = "BEGIN:VTIMEZONE\r\nTZID:Europe/Vienna\r\n";

        var result = IcsMerger.Merge("X", [Calendar(truncatedTz, Event("a@x"), Event("b@x"))]);

        Assert.Contains("UID:a@x", result);
        Assert.Contains("UID:b@x", result);
        Assert.DoesNotContain("BEGIN:VTIMEZONE", result);
    }

    [Fact]
    public void Merge_TruncatedEventDoesNotSwallowFollowingTimezone()
    {
        var truncated = "BEGIN:VEVENT\r\nUID:cut@x\r\n";

        var result = IcsMerger.Merge("X", [Calendar(truncated, Vienna, Event("ok@x"))]);

        Assert.DoesNotContain("cut@x", result);
        Assert.Contains("TZID:Europe/Vienna", result);
        Assert.Contains("UID:ok@x", result);
    }

    [Fact]
    public void Merge_KeepsTimezoneWithStandardAndDaylight()
    {
        var tz = "BEGIN:VTIMEZONE\r\nTZID:Europe/Vienna\r\nBEGIN:STANDARD\r\nTZOFFSETTO:+0100\r\nEND:STANDARD\r\n" +
                 "BEGIN:DAYLIGHT\r\nTZOFFSETTO:+0200\r\nEND:DAYLIGHT\r\nEND:VTIMEZONE\r\n";

        var result = IcsMerger.Merge("X", [Calendar(tz, Event("a@x"))]);

        Assert.Contains("BEGIN:STANDARD", result);
        Assert.Contains("BEGIN:DAYLIGHT", result);
        Assert.Contains("UID:a@x", result);
    }

    [Fact]
    public void Merge_DropsEventWithUnbalancedAlarmButKeepsNeighbours()
    {
        var bad = Event("bad@x", extra: "BEGIN:VALARM\r\nACTION:DISPLAY\r\n");

        var result = IcsMerger.Merge("X", [Calendar(Event("a@x"), bad, Event("b@x"))]);

        Assert.DoesNotContain("bad@x", result);
        Assert.DoesNotContain("VALARM", result);
        Assert.Contains("UID:a@x", result);
        Assert.Contains("UID:b@x", result);
        Assert.Equal(2, Count(result, "BEGIN:VEVENT"));
        Assert.Equal(2, Count(result, "END:VEVENT"));
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
    [InlineData("﻿BEGIN:VCALENDAR\nEND:VCALENDAR", true)]
    [InlineData("begin:vcalendar\r\nend:vcalendar", true)]
    [InlineData("  \n  BEGIN:VCALENDAR\nEND:VCALENDAR\n", true)]
    [InlineData("\uFEFF\r\n  \r\nBEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", true)]
    [InlineData("   BEGIN:VCALENDAR", true)]
    [InlineData("<html><body>Bitte anmelden</body></html>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeCalendar_DetectsCalendars(string? input, bool expected)
    {
        Assert.Equal(expected, IcsMerger.LooksLikeCalendar(input));
    }
}
