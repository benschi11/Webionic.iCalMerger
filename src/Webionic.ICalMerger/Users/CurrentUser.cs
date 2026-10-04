using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Webionic.ICalMerger.Users;

public sealed class CurrentUser(AuthenticationStateProvider authentication)
{
    public async Task<string> GetIdAsync()
    {
        var state = await authentication.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? throw new InvalidOperationException("Nicht angemeldet.");
    }
}
