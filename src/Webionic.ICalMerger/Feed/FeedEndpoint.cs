using Microsoft.AspNetCore.Diagnostics;

namespace Webionic.ICalMerger.Feed;

public static class FeedEndpoint
{
    private const int MinTokenLength = 16;
    private const int MaxTokenLength = 128;

    public static IEndpointRouteBuilder MapFeedEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/feed/{token}.ics", ["GET", "HEAD"], async (string token, FeedService feed, HttpContext http, CancellationToken ct) =>
        {
            // Offensichtlichen Müll abweisen, bevor die Datenbank gefragt wird.
            if (token.Length is < MinTokenLength or > MaxTokenLength)
            {
                return Failure(http, StatusCodes.Status404NotFound);
            }

            var result = await feed.BuildAsync(token, ct);
            if (result is null)
            {
                return Failure(http, StatusCodes.Status404NotFound);
            }

            if (result.Ics is null)
            {
                http.Response.Headers.RetryAfter = "60";
                return Failure(http, StatusCodes.Status503ServiceUnavailable);
            }

            http.Response.Headers.CacheControl = "public, max-age=300";
            if (HttpMethods.IsHead(http.Request.Method))
            {
                // Gleiche Header, kein Body (auch unter Test-Servern, die ihn nicht selbst entfernen).
                http.Response.ContentType = "text/calendar; charset=utf-8";
                return Results.Empty;
            }

            return Results.Text(result.Ics, "text/calendar; charset=utf-8");
        }).AllowAnonymous();

        return endpoints;
    }

    /// <summary>Fehlerantwort ohne Body und ohne Cache. Die HTML-Fehlerseite der App gehört nicht in einen Kalender-Feed.</summary>
    private static IResult Failure(HttpContext http, int statusCode)
    {
        http.Features.Get<IStatusCodePagesFeature>()?.Enabled = false;
        http.Response.Headers.CacheControl = "no-store";
        return Results.StatusCode(statusCode);
    }
}
