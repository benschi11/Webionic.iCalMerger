using Webionic.ICalMerger.Components.Ui;
using Webionic.ICalMerger.Data;

namespace Webionic.ICalMerger.Tests.Ui;

public sealed class StatusTextTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private static CalendarSource Ok(int minutesAgo = 3) => new() { LastSuccessAt = Now.AddMinutes(-minutesAgo), LastAttemptAt = Now.AddMinutes(-minutesAgo) };
    private static CalendarSource Stale(int hoursAgo = 2) => new() { LastSuccessAt = Now.AddHours(-hoursAgo), LastError = "Zeitüberschreitung" };
    private static CalendarSource Broken() => new() { LastError = "HTTP 500" };

    [Fact]
    public void Source_Ok_ShowsAge()
    {
        var line = StatusText.Source(Ok(), Now);

        Assert.Equal(SourceStatus.Ok, line.Status);
        Assert.Equal("vor 3 Min. abgerufen", line.Text);
        Assert.Null(line.Detail);
    }

    [Fact]
    public void Source_Stale_ShowsErrorAndLastSuccess()
    {
        var line = StatusText.Source(Stale(), Now);

        Assert.Equal(SourceStatus.Stale, line.Status);
        Assert.Equal("Zeitüberschreitung", line.Text);
        Assert.Equal("Letzter Erfolg: vor 2 Std.", line.Detail);
    }

    [Fact]
    public void Source_Error_ShowsErrorAndNeverSucceeded()
    {
        var line = StatusText.Source(Broken(), Now);

        Assert.Equal(SourceStatus.Error, line.Status);
        Assert.Equal("HTTP 500", line.Text);
        Assert.Equal("Noch nie erfolgreich abgerufen", line.Detail);
    }

    [Fact]
    public void Source_Unknown_IsNotFetchedYet()
    {
        var line = StatusText.Source(new CalendarSource(), Now);

        Assert.Equal(SourceStatus.Unknown, line.Status);
        Assert.Equal("Noch nicht abgerufen", line.Text);
    }

    [Fact]
    public void Calendar_WithoutSources_HasNoSources()
    {
        var line = StatusText.Calendar([], Now);

        Assert.Equal(SourceStatus.Unknown, line.Status);
        Assert.Equal("Keine Quellen", line.Text);
    }

    [Fact]
    public void Calendar_AllOk()
    {
        var line = StatusText.Calendar([Ok(), Ok()], Now);

        Assert.Equal(SourceStatus.Ok, line.Status);
        Assert.Equal("Alle Quellen erreichbar", line.Text);
    }

    [Fact]
    public void Calendar_WithBrokenSource_CountsBrokenOnes()
    {
        Assert.Equal("1 Quelle fehlerhaft", StatusText.Calendar([Ok(), Broken(), Stale()], Now).Text);
        Assert.Equal("2 Quellen fehlerhaft", StatusText.Calendar([Broken(), Broken()], Now).Text);
        Assert.Equal(SourceStatus.Error, StatusText.Calendar([Ok(), Broken()], Now).Status);
    }

    [Fact]
    public void Calendar_WithStaleSourcesOnly_ShowsOldestState()
    {
        var line = StatusText.Calendar([Ok(), Stale(hoursAgo: 2), Stale(hoursAgo: 5)], Now);

        Assert.Equal(SourceStatus.Stale, line.Status);
        Assert.Equal("Zwischenspeicher, Stand: vor 5 Std.", line.Text);
    }

    [Theory]
    [InlineData(SourceStatus.Ok, "ok")]
    [InlineData(SourceStatus.Stale, "warn")]
    [InlineData(SourceStatus.Error, "err")]
    [InlineData(SourceStatus.Unknown, "off")]
    public void Css_MapsStatusToClass(SourceStatus status, string expected) => Assert.Equal(expected, StatusText.Css(status));
}
