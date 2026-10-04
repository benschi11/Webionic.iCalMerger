using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Webionic.ICalMerger.Data;

namespace Microsoft.AspNetCore.Routing;

internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    // Der einzige zusätzliche Endpunkt: Abmelden. Der Parameter aus dem Formular
    // erzwingt die Antiforgery-Prüfung.
    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var accountGroup = endpoints.MapGroup("/Account");

        accountGroup.MapPost("/Logout", async (
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromForm] string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            return TypedResults.LocalRedirect("~/Account/Login");
        });

        return accountGroup;
    }
}
