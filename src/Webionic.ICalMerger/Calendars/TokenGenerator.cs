using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace Webionic.ICalMerger.Calendars;

public static class TokenGenerator
{
    /// <summary>32 Zufallsbytes als Base64Url (43 Zeichen).</summary>
    public static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
}
