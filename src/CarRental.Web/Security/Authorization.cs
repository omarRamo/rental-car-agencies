using System.Collections.Concurrent;
using System.Security.Claims;
using CarRental.Application.Identity.Contracts;
using CarRental.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Security;

public static class AuthorizationPolicies
{
    /// <summary>Back office: an authenticated user who signed in with two-factor authentication (<c>amr=mfa</c>).</summary>
    public const string AdminArea = "AdminArea";
}

/// <summary>The sign-in used a second factor (ASP.NET Core Identity sets the <c>amr</c> claim to <c>mfa</c>).</summary>
public sealed class MfaRequirement : IAuthorizationRequirement
{
    public const string AuthenticationMethodClaimType = "amr";
    public const string MfaValue = "mfa";
}

/// <summary>The user holds a permission claim (from a role preset or granted individually).</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Met by a sign-in with the second factor; by any signed-in user when <c>Identity:TwoFactor:Required</c> is off (a
/// developer's machine only, see <see cref="TwoFactorOptions"/>).
/// </summary>
internal sealed class MfaAuthorizationHandler(IOptions<TwoFactorOptions> options) : AuthorizationHandler<MfaRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MfaRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!options.Value.Required || context.User.HasClaim(MfaRequirement.AuthenticationMethodClaimType, MfaRequirement.MfaValue))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        if (context.User.HasClaim(Permissions.ClaimType, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Resolves <c>Permission:{name}</c> policies on demand: authenticated, MFA sign-in and the permission; and
/// <c>Module:{module}</c> policies (ADR-0067): authenticated, MFA sign-in and the module on for the agency of the session.
/// Unknown permission or module names fail closed (no policy), other names go to the default provider.
/// </summary>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);
    private readonly ConcurrentDictionary<string, AuthorizationPolicy> _policies = new(StringComparer.Ordinal);

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (PermissionPolicy.TryGetPermission(policyName, out var permission))
        {
            if (!Permissions.IsKnown(permission))
            {
                throw new InvalidOperationException($"Unknown permission '{permission}' in policy '{policyName}'.");
            }

            return Task.FromResult<AuthorizationPolicy?>(_policies.GetOrAdd(
                policyName,
                _ => new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new MfaRequirement(), new PermissionRequirement(permission))
                    .Build()));
        }

        if (policyName.StartsWith(ModulePolicy.Prefix, StringComparison.Ordinal))
        {
            if (!ModulePolicy.TryGetModule(policyName, out var module))
            {
                throw new InvalidOperationException($"Unknown module in policy '{policyName}'.");
            }

            return Task.FromResult<AuthorizationPolicy?>(_policies.GetOrAdd(
                policyName,
                _ => new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new MfaRequirement(), new ModuleRequirement(module.Value))
                    .Build()));
        }

        return _fallback.GetPolicyAsync(policyName);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();
}

/// <summary>
/// A signed-in user without an MFA session who opens the back office is sent to enroll an authenticator (two-factor
/// not yet enabled) or to sign in again with the second factor. Other failures keep the default behavior.
/// </summary>
internal sealed class AdminAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    public const string EnrollPath = "/account/manage/enable-authenticator";
    public const string LoginPath = "/account/login";

    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Forbidden
            && context.User.Identity?.IsAuthenticated == true
            && authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<MfaRequirement>().Any() == true)
        {
            var userManager = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.GetUserAsync(context.User);
            var returnUrl = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
            var target = user is not null && !await userManager.GetTwoFactorEnabledAsync(user) ? EnrollPath : LoginPath;
            context.Response.Redirect(QueryHelpers.AddQueryString(target, "ReturnUrl", returnUrl));
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    }

    public static string? GetDisplayName(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirstValue(CarRentalClaimTypes.DisplayName) ?? principal.Identity?.Name;
    }

    /// <summary>The agency the session works for (ADR-0067), <see langword="null"/> for the support outside any agency.</summary>
    public static Guid? GetAgencyId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Guid.TryParse(principal.FindFirstValue(CarRentalClaimTypes.Agency), out var id) && id != Guid.Empty ? id : null;
    }

    /// <summary>True for the support of the platform (<see cref="Permissions.PlatformAgencies"/>).</summary>
    public static bool IsSupport(this ClaimsPrincipal principal) => principal.HasPermission(Permissions.PlatformAgencies);

    public static IEnumerable<string> GetPermissions(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindAll(Permissions.ClaimType).Select(c => c.Value).Distinct(StringComparer.Ordinal);
    }

    public static bool HasPermission(this ClaimsPrincipal principal, string permission)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.HasClaim(Permissions.ClaimType, permission);
    }

    public static bool IsMfaSession(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.HasClaim(MfaRequirement.AuthenticationMethodClaimType, MfaRequirement.MfaValue);
    }
}
