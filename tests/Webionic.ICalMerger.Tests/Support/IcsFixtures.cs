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
