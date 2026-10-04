namespace Webionic.ICalMerger.Data;

public enum SourceStatus
{
    /// <summary>Noch nie abgerufen.</summary>
    Unknown,
    /// <summary>Letzter Abruf erfolgreich.</summary>
    Ok,
    /// <summary>Letzter Abruf fehlgeschlagen, aber früher erfolgreich: der Feed liefert die letzten bekannten Daten.</summary>
    Stale,
    /// <summary>Fehler und noch nie erfolgreich: die Quelle fehlt im Feed.</summary>
    Error,
}

public static class SourceStatusExtensions
{
    public static SourceStatus GetStatus(this CalendarSource source) => (source.LastError, source.LastSuccessAt) switch
    {
        (null, null) => SourceStatus.Unknown,
        (null, _) => SourceStatus.Ok,
        (_, null) => SourceStatus.Error,
        _ => SourceStatus.Stale,
    };

    /// <summary>Der schlechteste Status gewinnt. Ohne Quellen oder ohne jeden Abruf: <see cref="SourceStatus.Unknown"/>.</summary>
    public static SourceStatus Summarize(IEnumerable<CalendarSource> sources)
    {
        var statuses = sources.Select(GetStatus).ToList();
        if (statuses.Contains(SourceStatus.Error)) return SourceStatus.Error;
        if (statuses.Contains(SourceStatus.Stale)) return SourceStatus.Stale;
        if (statuses.Contains(SourceStatus.Ok)) return SourceStatus.Ok;
        return SourceStatus.Unknown;
    }
}
