using System.Collections.Concurrent;
using Webionic.ICalMerger.Fetching;

namespace Webionic.ICalMerger.Tests.Support;

internal sealed class FakeFetcher : ICalendarFetcher
{
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<string>>> _responses = new();
    private readonly ConcurrentDictionary<string, int> _calls = new();

    public void Set(string url, string ics) => _responses[url] = _ => Task.FromResult(ics);

    public void SetAsync(string url, Func<CancellationToken, Task<string>> response) => _responses[url] = response;

    public void Fail(string url, string message = "Quelle nicht erreichbar") =>
        _responses[url] = _ => throw new FetchException(message);

    public int CallCount(string url) => _calls.GetValueOrDefault(url);

    public Task<string> FetchAsync(string url, CancellationToken ct)
    {
        _calls.AddOrUpdate(url, 1, (_, n) => n + 1);
        return _responses.TryGetValue(url, out var response)
            ? response(ct)
            : throw new FetchException("HTTP 404");
    }
}
