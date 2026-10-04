using Webionic.ICalMerger.Data;

namespace Webionic.ICalMerger.Components.Ui;

public readonly record struct StatusLine(SourceStatus Status, string Text, string? Detail = null);

/// <summary>Deutsche Statustexte für Quellen und Kalender, an einer Stelle und testbar.</summary>
public static class StatusText
{
    public static StatusLine Source(CalendarSource source, DateTime nowUtc)
    {
        var status = source.GetStatus();
        return status switch
        {
            SourceStatus.Ok => new(status, $"{RelativeTime.Ago(source.LastSuccessAt, nowUtc)} abgerufen"),
            SourceStatus.Stale => new(status, source.LastError!, $"Letzter Erfolg: {RelativeTime.Ago(source.LastSuccessAt, nowUtc)}"),
            SourceStatus.Error => new(status, source.LastError!, "Noch nie erfolgreich abgerufen"),
            _ => new(status, "Noch nicht abgerufen"),
        };
    }

    public static StatusLine Calendar(IReadOnlyCollection<CalendarSource> sources, DateTime nowUtc)
    {
        if (sources.Count == 0) return new(SourceStatus.Unknown, "Keine Quellen");

        var status = SourceStatusExtensions.Summarize(sources);
        switch (status)
        {
            case SourceStatus.Error:
                var broken = sources.Count(s => s.GetStatus() == SourceStatus.Error);
                return new(status, broken == 1 ? "1 Quelle fehlerhaft" : $"{broken} Quellen fehlerhaft");
            case SourceStatus.Stale:
                var oldest = sources.Where(s => s.GetStatus() == SourceStatus.Stale).Min(s => s.LastSuccessAt);
                return new(status, $"Zwischenspeicher, Stand: {RelativeTime.Ago(oldest, nowUtc)}");
            case SourceStatus.Ok:
                return new(status, "Alle Quellen erreichbar");
            default:
                return new(status, "Noch nicht abgerufen");
        }
    }

    public static string Css(SourceStatus status) => status switch
    {
        SourceStatus.Ok => "ok",
        SourceStatus.Stale => "warn",
        SourceStatus.Error => "err",
        _ => "off",
    };
}
