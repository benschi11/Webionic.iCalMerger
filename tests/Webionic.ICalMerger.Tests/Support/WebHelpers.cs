using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Webionic.ICalMerger.Tests.Support;

internal static partial class WebHelpers
{
    [GeneratedRegex("<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryPattern();

    /// <summary>Client ohne automatisches Folgen von Weiterleitungen (Cookies werden gehalten).</summary>
    public static HttpClient NewClient(this AppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public static string AntiforgeryToken(string html)
    {
        var match = AntiforgeryPattern().Match(html);
        Assert.True(match.Success, "Kein Antiforgery-Token im HTML gefunden.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>Lädt die Seite (für Cookie und Token) und sendet dann das Formular mit dem angegebenen Handler-Namen.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string pathAndQuery, string handler, Dictionary<string, string> fields)
    {
        var page = await client.GetStringAsync(pathAndQuery);
        fields["__RequestVerificationToken"] = AntiforgeryToken(page);
        fields["_handler"] = handler;
        return await client.PostAsync(pathAndQuery, new FormUrlEncodedContent(fields));
    }

    public static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        PostFormAsync(client, "/Account/Login", "login", new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.RememberMe"] = "false",
        });
}
