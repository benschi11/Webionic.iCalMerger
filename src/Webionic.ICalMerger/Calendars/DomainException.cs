namespace Webionic.ICalMerger.Calendars;

/// <summary>Fehler, deren (deutsche) Meldung direkt in der UI angezeigt werden darf.</summary>
public class DomainException(string message) : Exception(message);
