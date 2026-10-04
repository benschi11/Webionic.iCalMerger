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
