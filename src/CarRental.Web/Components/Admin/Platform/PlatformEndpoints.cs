using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Identity.Users;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Identity;
using CarRental.Web.Hosting;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CarRental.Web.Components.Admin.Platform;

/// <summary>Addresses of the platform pages (support only, ADR-0067).</summary>
public static class PlatformPaths
{
    /// <summary>The agencies of the platform: the home of the support outside any agency.</summary>
    public const string Agencies = "/admin/platform";

    /// <summary>POST <c>agencyId</c>: the support enters that agency's back office.</summary>
    public const string Enter = "/admin/platform/enter";

    /// <summary>POST: the support leaves the agency they entered and comes back to the platform pages.</summary>
    public const string Leave = "/admin/platform/leave";

    /// <summary>True for an address of the pages that work outside any agency (platform pages, outbox, background jobs).</summary>
    public static bool IsPlatformPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var path = "/" + relativePath.Split('?', '#')[0].TrimStart('/');
        return path.Equals(Agencies, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Agencies + "/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/admin/outbox", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/admin/jobs", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Entering and leaving an agency (support only): the choice is stored on the support account, the session is renewed
/// with the new agency claim, and the browser goes to the back office of the agency (or back to the platform pages).
/// Form posts with antiforgery token.
/// </summary>
internal static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(PlatformPaths.Enter, async (
                HttpContext context,
                [FromForm] Guid agencyId,
                [FromServices] SignInManager<ApplicationUser> signInManager) =>
            {
                var entered = await PlatformScopes.RunAcrossAgenciesAsync<SupportSessionService, Result>(
                    context,
                    (sessions, ct) => sessions.EnterAgencyAsync(agencyId, ct));
                if (entered.IsFailure)
                {
                    return Results.LocalRedirect(PlatformPaths.Agencies);
                }

                await RefreshAsync(context, signInManager);
                return Results.LocalRedirect("/admin");
            })
            .RequireAuthorization(PermissionPolicy.For(Permissions.PlatformAgencies));

        endpoints.MapPost(PlatformPaths.Leave, async (
                HttpContext context,
                [FromServices] SignInManager<ApplicationUser> signInManager) =>
            {
                await PlatformScopes.RunAcrossAgenciesAsync<SupportSessionService, Result>(
                    context,
                    (sessions, ct) => sessions.LeaveAgencyAsync(ct));
                await RefreshAsync(context, signInManager);
                return Results.LocalRedirect(PlatformPaths.Agencies);
            })
            .RequireAuthorization(PermissionPolicy.For(Permissions.PlatformAgencies));

        return endpoints;
    }

    /// <summary>Renews the session cookie with the new agency claim, keeping how the support signed in.</summary>
    private static async Task RefreshAsync(HttpContext context, SignInManager<ApplicationUser> signInManager)
    {
        if (await signInManager.UserManager.GetUserAsync(context.User) is { } user)
        {
            await signInManager.RefreshSignInAsync(user);
        }
    }
}

/// <summary>
/// Runs an application service across agencies on behalf of the signed-in support (ADR-0067), whatever agency their
/// session works for: the platform operations (agencies, entering one) are never limited to the agency entered.
/// </summary>
internal static class PlatformScopes
{
    public static async Task<TResult> RunAcrossAgenciesAsync<TService, TResult>(
        HttpContext context,
        Func<TService, CancellationToken, Task<TResult>> operation)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        var ipResolver = context.RequestServices.GetRequiredService<ClientIpResolver>();
        var actor = UserContextFactory.FromPrincipal(context.User, ipResolver.Resolve(context), CorrelationIds.Current(context));
        if (!actor.AllAgencies)
        {
            throw new InvalidOperationException("Only the support works across agencies.");
        }

        await using var scope = context.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(actor with { AgencyId = null });
        return await operation(scope.ServiceProvider.GetRequiredService<TService>(), context.RequestAborted);
    }
}
