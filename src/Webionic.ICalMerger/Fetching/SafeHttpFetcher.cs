using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Webionic.ICalMerger.Fetching;

public interface ICalendarFetcher
{
    /// <exception cref="FetchException">Abruf nicht möglich. Die Meldung ist deutsch und enthält nie die URL.</exception>
    Task<string> FetchAsync(string url, CancellationToken ct);
}

public sealed class FetchException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record FetcherOptions(TimeSpan Timeout, long MaxBytes)
{
    public static FetcherOptions Default { get; } = new(TimeSpan.FromSeconds(10), 10 * 1024 * 1024);
}

public sealed class SafeHttpFetcher(HttpClient client, FetcherOptions? options = null) : ICalendarFetcher
{
    private readonly FetcherOptions _options = options ?? FetcherOptions.Default;

    public static Uri NormalizeUrl(string url)
    {
        var trimmed = (url ?? "").Trim();
        foreach (var scheme in new[] { "webcals://", "webcal://" })
        {
            if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "https://" + trimmed[scheme.Length..];
                break;
            }
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new FetchException("Ungültige URL");
        }

        return uri;
    }

    /// <summary>
    /// Handler, der die Ziel-IP beim Verbindungsaufbau prüft. Das deckt auch Redirects und DNS-Rebinding ab,
    /// weil jede Verbindung neu geprüft wird.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(Func<IPAddress, bool>? isAllowed = null)
    {
        isAllowed ??= IpGuard.IsPublic;

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var allowed = addresses.Where(isAllowed).ToArray();
                if (allowed.Length == 0)
                {
                    throw new BlockedAddressException();
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    public async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        var uri = NormalizeUrl(url);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new FetchException($"HTTP {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength > _options.MaxBytes)
            {
                throw new FetchException("Antwort ist zu groß");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await ReadLimitedAsync(stream, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FetchException("Zeitüberschreitung beim Abruf");
        }
        catch (HttpRequestException ex)
        {
            throw new FetchException(
                ex.GetBaseException() is BlockedAddressException
                    ? "Adresse nicht erlaubt (interne Netzwerke sind gesperrt)"
                    : "Quelle nicht erreichbar",
                null);
        }
        catch (Exception ex) when (ex is not (FetchException or OperationCanceledException))
        {
            // Inner-Exceptions werden bewusst verworfen: HttpClient-Meldungen enthalten Host und Port der Quelle.
            // IOException, InvalidDataException, SocketException, Dekodierfehler usw.: Meldung ohne URL.
            throw new FetchException("Antwort konnte nicht gelesen werden");
        }
    }

    private async Task<string> ReadLimitedAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > _options.MaxBytes)
            {
                throw new FetchException("Antwort ist zu groß");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private sealed class BlockedAddressException : Exception;
}
