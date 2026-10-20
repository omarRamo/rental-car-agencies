using CarRental.Application.Common.Auditing;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Identity;
using CarRental.Web.Localization;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CarRental.Web.Components.Account;

internal static class AccountEndpoints
{
    public const string LogoutPath = "/account/logout";

    /// <summary>Sign-out (POST with antiforgery token, from the account pages and the back office user menu).</summary>
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(LogoutPath, async (
            HttpContext context,
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromServices] IAuditTrail auditTrail,
            [FromForm] string? returnUrl) =>
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                await auditTrail.RecordAsync(new AuditEvent(AuditActions.Logout)
                {
                    EntityType = "User",
                    EntityId = context.User.GetUserId()?.ToString(),
                });
            }

            await signInManager.SignOutAsync();
            return TypedResults.LocalRedirect(LocalUrls.IsLocal(returnUrl) ? returnUrl! : "/account/login");
        });

        return endpoints;
    }
}
