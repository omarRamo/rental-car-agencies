using System.Diagnostics;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Infrastructure.Identity;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Hosting;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace CarRental.Web.Security;

/// <summary>
/// Ambient actor of HTTP requests (static pages, minimal APIs): the request principal, client IP and trace id. On the
/// public site, a visitor of the agency of the site, signed in or not (ADR-0067).
/// </summary>
/// <remarks>
/// Only the scope of the request itself takes it: a scope created while a request runs (a cache load, a step run as the
/// system) sets its own actor, else it is anonymous and sees no agency data — it never inherits the agency of whoever
/// happened to trigger it.
/// </remarks>
internal sealed class HttpUserContextProvider(IHttpContextAccessor accessor, ClientIpResolver ipResolver, IServiceProvider scope) : IAmbientUserContextProvider
{
    public UserContextSnapshot? GetCurrent()
    {
        var httpContext = accessor.HttpContext;
        if (httpContext is null || !ReferenceEquals(httpContext.RequestServices, scope))
        {
            return null;
        }

        var ipAddress = ipResolver.Resolve(httpContext);
        var correlationId = CorrelationIds.Current(httpContext);
        return PublicSiteAgency.Of(httpContext) is { } agencyId
            ? UserContextSnapshot.Anonymous(ipAddress, correlationId) with { AgencyId = agencyId }
            : UserContextFactory.FromPrincipal(httpContext.User, ipAddress, correlationId);
    }
}

internal static class UserContextFactory
{
    /// <summary>
    /// The actor of a principal: a signed-in user works for the agency of their session (<see cref="CarRentalClaimTypes.Agency"/>),
    /// across agencies when they hold <see cref="Permissions.PlatformAgencies"/> (support); a visitor works for the agency
    /// of the public site they browse (<paramref name="publicAgencyId"/>), or none (ADR-0067).
    /// </summary>
    public static UserContextSnapshot FromPrincipal(
        System.Security.Claims.ClaimsPrincipal principal,
        string? ipAddress,
        string? correlationId,
        Guid? publicAgencyId = null) =>
        principal.Identity?.IsAuthenticated == true && principal.GetUserId() is { } userId
            ? UserContextSnapshot.ForUser(userId, principal.Identity.Name, principal.GetPermissions(), ipAddress, correlationId) with
            {
                AgencyId = principal.GetAgencyId(),
                AllAgencies = principal.HasPermission(Permissions.PlatformAgencies),
            }
            : UserContextSnapshot.Anonymous(ipAddress, correlationId) with { AgencyId = publicAgencyId };
}

internal static class CorrelationIds
{
    public static string? Current(HttpContext? httpContext = null) =>
        Activity.Current?.TraceId.ToString() ?? httpContext?.TraceIdentifier;
}

/// <summary>
/// Sets the actor of a Blazor circuit when it opens (user from the authentication state, IP of the connection), so
/// that interactive components and <see cref="IOperationRunner"/> act on behalf of the signed-in user.
/// </summary>
internal sealed class UserContextCircuitHandler(
    ScopedUserContext userContext,
    AuthenticationStateProvider authenticationStateProvider,
    IHttpContextAccessor accessor,
    ClientIpResolver ipResolver) : CircuitHandler
{
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        var httpContext = accessor.HttpContext;
        var ipAddress = httpContext is null ? null : ipResolver.Resolve(httpContext);
        userContext.Initialize(UserContextFactory.FromPrincipal(state.User, ipAddress, CorrelationIds.Current(httpContext)));
    }
}

/// <summary>
/// Runs an application service in a dedicated DI scope (fresh DbContext, unit of work) on behalf of the current actor.
/// Interactive components must call application services through this runner: services of a Blazor circuit scope
/// live as long as the circuit, and a DbContext must not.
/// </summary>
public interface IOperationRunner
{
    Task<TResult> RunAsync<TService, TResult>(
        Func<TService, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        where TService : notnull;

    /// <summary>
    /// Runs the operation across agencies (ADR-0067): for the platform pages of the support, whatever agency their session
    /// works for. Throws for anyone else.
    /// </summary>
    Task<TResult> RunAcrossAgenciesAsync<TService, TResult>(
        Func<TService, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        where TService : notnull;
}

/// <summary>Base URL of the current request or circuit, carried into operation scopes (e-mail links).</summary>
public sealed class RequestOrigin
{
    public string? BaseUri { get; set; }
}

internal sealed class OperationRunner(
    IServiceScopeFactory scopeFactory,
    ScopedUserContext userContext,
    NavigationManager navigationManager) : IOperationRunner
{
    private static readonly ActivitySource Source = new("CarRental.Web");

    public Task<TResult> RunAsync<TService, TResult>(
        Func<TService, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        where TService : notnull =>
        RunAsync(operation, acrossAgencies: false, cancellationToken);

    public Task<TResult> RunAcrossAgenciesAsync<TService, TResult>(
        Func<TService, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        where TService : notnull
    {
        if (!userContext.Snapshot.AllAgencies)
        {
            throw new InvalidOperationException("Only the support works across agencies.");
        }

        return RunAsync(operation, acrossAgencies: true, cancellationToken);
    }

    private async Task<TResult> RunAsync<TService, TResult>(
        Func<TService, CancellationToken, Task<TResult>> operation,
        bool acrossAgencies,
        CancellationToken cancellationToken)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var activity = Source.StartActivity($"operation {typeof(TService).Name}");

        await using var scope = scopeFactory.CreateAsyncScope();
        var correlationId = activity?.TraceId.ToString() ?? Guid.CreateVersion7().ToString("N");
        var actor = userContext.Snapshot with { CorrelationId = correlationId };
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(acrossAgencies ? actor with { AgencyId = null } : actor);
        scope.ServiceProvider.GetRequiredService<RequestOrigin>().BaseUri = TryGetBaseUri();

        var service = scope.ServiceProvider.GetRequiredService<TService>();
        return await operation(service, cancellationToken);
    }

    private string? TryGetBaseUri()
    {
        try
        {
            return navigationManager.BaseUri;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
