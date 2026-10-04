namespace Webionic.ICalMerger.Calendars;

public sealed class AppLimits
{
    public int MaxCalendarsPerUser { get; set; } = 10;
    public int MaxSourcesPerCalendar { get; set; } = 20;
}
