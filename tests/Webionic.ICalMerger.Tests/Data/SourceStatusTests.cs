using Webionic.ICalMerger.Data;

namespace Webionic.ICalMerger.Tests.Data;

public sealed class SourceStatusTests
{
    private static readonly DateTime Earlier = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);

    private static CalendarSource Source(string? error, DateTime? success) => new() { LastError = error, LastSuccessAt = success };

    [Fact]
    public void NeverFetched_IsUnknown() => Assert.Equal(SourceStatus.Unknown, Source(null, null).GetStatus());

    [Fact]
    public void FetchedWithoutError_IsOk() => Assert.Equal(SourceStatus.Ok, Source(null, Earlier).GetStatus());

    [Fact]
    public void ErrorAfterEarlierSuccess_IsStale() => Assert.Equal(SourceStatus.Stale, Source("HTTP 503", Earlier).GetStatus());

    [Fact]
    public void ErrorWithoutAnySuccess_IsError() => Assert.Equal(SourceStatus.Error, Source("HTTP 500", null).GetStatus());

    [Fact]
    public void Summarize_ReturnsWorstStatus()
    {
        var ok = Source(null, Earlier);
        var stale = Source("x", Earlier);
        var error = Source("y", null);
        var unknown = Source(null, null);

        Assert.Equal(SourceStatus.Error, SourceStatusExtensions.Summarize([ok, stale, error, unknown]));
        Assert.Equal(SourceStatus.Stale, SourceStatusExtensions.Summarize([ok, stale, unknown]));
        Assert.Equal(SourceStatus.Ok, SourceStatusExtensions.Summarize([ok, unknown]));
        Assert.Equal(SourceStatus.Unknown, SourceStatusExtensions.Summarize([unknown]));
        Assert.Equal(SourceStatus.Unknown, SourceStatusExtensions.Summarize([]));
    }
}
