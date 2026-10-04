using System.Globalization;

namespace Webionic.ICalMerger.Components.Ui;

public static class RelativeTime
{
    /// <summary>Relative deutsche Zeitangabe. Absolute Zeiten vermeiden die Zeitzone, die der Container nicht kennt.</summary>
    public static string Ago(DateTime? utc, DateTime nowUtc)
    {
        if (utc is null) return "noch nie";

        // SQLite liefert Kind=Unspecified, gespeichert wird UTC.
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
