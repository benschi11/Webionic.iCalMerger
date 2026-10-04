using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace Webionic.ICalMerger.Tests.Support;

/// <summary>Minimaler Kestrel-Server auf Loopback mit zufälligem Port.</summary>
internal sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LocalHttpServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public string BaseUrl { get; }

    public static async Task<LocalHttpServer> StartAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return new LocalHttpServer(app, app.Urls.First());
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
