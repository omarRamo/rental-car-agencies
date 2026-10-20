using System.Diagnostics;
using CarRental.Application.Common.Security;
using CarRental.Web.Hosting;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace CarRental.Web.Security;

/// <summary>Ambient actor of HTTP requests (static pages, minimal APIs): the request principal, client IP and trace id.</summary>
internal sealed class HttpUserContextProvider(IHttpContextAccessor accessor, ClientIpResolver ipResolver) : IAmbientUserContextProvider
{
    public UserContextSnapshot? GetCurrent()
    {
        var httpContext = accessor.HttpContext;
        return httpContext is null ? null : UserContextFactory.FromPrincipal(httpContext.User, ipResolver.Resolve(httpContext), CorrelationIds.Current(httpContext));
    }
}

internal static class UserContextFactory
{
    public static UserContextSnapshot FromPrincipal(System.Security.Claims.ClaimsPrincipal principal, string? ipAddress, string? correlationId) =>
        principal.Identity?.IsAuthenticated == true && principal.GetUserId() is { } userId
            ? UserContextSnapshot.ForUser(userId, principal.Identity.Name, principal.GetPermissions(), ipAddress, correlationId)
            : UserContextSnapshot.Anonymous(ipAddress, correlationId);
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

    public async Task<TResult> RunAsync<TService, TResult>(
        Func<TService, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var activity = Source.StartActivity($"operation {typeof(TService).Name}");

        await using var scope = scopeFactory.CreateAsyncScope();
        var correlationId = activity?.TraceId.ToString() ?? Guid.CreateVersion7().ToString("N");
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(userContext.Snapshot with { CorrelationId = correlationId });
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
