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
