using Webionic.ICalMerger.Components.Ui;

namespace Webionic.ICalMerger.Tests.Ui;

public sealed class RelativeTimeTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "gerade eben")]
    [InlineData(59, "gerade eben")]
    [InlineData(60, "vor 1 Min.")]
    [InlineData(3 * 60, "vor 3 Min.")]
    [InlineData(59 * 60, "vor 59 Min.")]
    [InlineData(60 * 60, "vor 1 Std.")]
    [InlineData(23 * 3600, "vor 23 Std.")]
    [InlineData(24 * 3600, "vor 1 Tag")]
    [InlineData(3 * 24 * 3600, "vor 3 Tagen")]
    [InlineData(7 * 24 * 3600, "am 27.09.2026")]
    public void Ago_FormatsAgeInGerman(int secondsAgo, string expected)
    {
        Assert.Equal(expected, RelativeTime.Ago(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void Ago_Null_IsNever() => Assert.Equal("noch nie", RelativeTime.Ago(null, Now));

    [Fact]
    public void Ago_FutureTimestamp_IsJustNow() => Assert.Equal("gerade eben", RelativeTime.Ago(Now.AddMinutes(5), Now));

    [Fact]
    public void Ago_UnspecifiedKind_IsTreatedAsUtc()
    {
        var fromDb = DateTime.SpecifyKind(Now.AddMinutes(-3), DateTimeKind.Unspecified);
        Assert.Equal("vor 3 Min.", RelativeTime.Ago(fromDb, Now));
    }
}
