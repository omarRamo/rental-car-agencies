using System.Text.RegularExpressions;
using CarRental.Web.Components.Public;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Security;

/// <summary>
/// Inventory of the endpoints of the real application (security review, ADR-0062): every endpoint requires an
/// authorization policy, or is anonymous with a rate limiting policy, or belongs to the short list below — content that
/// costs nothing to serve, each entry with its reason. A new anonymous endpoint without rate limit fails here.
/// The health probes are middleware (throttled before them), not endpoints.
/// </summary>
public sealed partial class EndpointInventoryTests(IntegrationHost host)
{
    /// <summary>Anonymous endpoints without rate limit, by route pattern, with the reason they need none.</summary>
    private static readonly Dictionary<string, string> Unlimited = new(StringComparer.Ordinal)
    {
        ["/"] = "redirects to the culture of the browser, no data read",
        ["/culture/set"] = "sets the culture cookie, no data read",
        ["/sitemap.xml"] = "built from the settings, cached one hour",
        ["/robots.txt"] = "built from the settings, cached one hour",
        ["/account/logout"] = "signs the current session out, antiforgery token required",
        ["/error"] = "error page of the exception handler",
        ["/status/{StatusCode:int}"] = "status code pages (re-executed)",
        ["/not-found"] = "page of the router for unknown addresses",
        ["/{**path:file}"] = "fallback of the static files of the build output (development and tests), files only",
    };

    [Fact]
    public void Every_endpoint_is_authorized_rate_limited_or_known_to_be_free()
    {
        var unprotected = Endpoints()
            .Where(e => !RequiresAuthorization(e) && !IsRateLimited(e) && !IsFreeContent(e))
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        unprotected.ShouldBeEmpty(
            "Anonymous endpoints without rate limit: require an authorization policy, add .RequireRateLimiting(...) / [EnableRateLimiting(...)], " +
            "or list them in EndpointInventoryTests with the reason (and in docs/securite/revue-owasp-2026-10.md).");
    }

    [Fact]
    public void Every_back_office_endpoint_requires_an_mfa_policy()
    {
        var unprotected = Endpoints()
            .Where(e => Pattern(e).StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null
                        || !e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy is { Length: > 0 }))
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Endpoints().ShouldContain(e => Pattern(e).StartsWith("/admin", StringComparison.OrdinalIgnoreCase));
        unprotected.ShouldBeEmpty("Back office endpoints require the AdminArea policy or a Permission:* policy (both demand an MFA session).");
    }

    [Fact]
    public void The_inbound_and_public_apis_are_rate_limited()
    {
        string[] prefixes = ["/api", "/webhooks", "/payments", "/media/categories"];
        var unlimited = Endpoints()
            .Where(e => prefixes.Any(p => Pattern(e).StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Where(e => !IsRateLimited(e))
            .Select(Describe)
            .ToList();

        Endpoints().ShouldContain(e => Pattern(e).StartsWith("/webhooks", StringComparison.OrdinalIgnoreCase));
        unlimited.ShouldBeEmpty();
    }

    private List<RouteEndpoint> Endpoints() =>
        [.. host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];

    private static string Pattern(RouteEndpoint endpoint)
    {
        var raw = endpoint.RoutePattern.RawText ?? string.Empty;
        return raw.StartsWith('/') ? raw : "/" + raw;
    }

    private static string Describe(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } verbs ? string.Join(",", verbs) : "*";
        return $"{methods} {Pattern(endpoint)} ({endpoint.DisplayName})";
    }

    private static bool RequiresAuthorization(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null && endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any();

    private static bool IsRateLimited(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>() is not null && endpoint.Metadata.GetMetadata<DisableRateLimitingAttribute>() is null;

    private static bool IsFreeContent(RouteEndpoint endpoint)
    {
        var pattern = Pattern(endpoint);
        return Unlimited.ContainsKey(pattern)
               || StaticFile().IsMatch(pattern) // static assets (fingerprinted files, Blazor and MudBlazor scripts)
               || pattern.StartsWith("/_blazor", StringComparison.Ordinal) // SignalR hub of the circuits: each page authorizes itself
               || pattern.StartsWith("/_framework", StringComparison.Ordinal)
               || pattern.StartsWith("/dev/", StringComparison.Ordinal) // simulated gateway pages, Development and Testing only
               || endpoint.Metadata.GetMetadata<CacheablePublicPageAttribute>() is not null; // fixed public pages, cached 5 minutes
    }

    /// <summary>A last segment with a file extension (<c>css/site.css</c>, <c>_content/MudBlazor/MudBlazor.min.js</c>).</summary>
    [GeneratedRegex(@"\.[A-Za-z0-9]{1,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex StaticFile();
}
