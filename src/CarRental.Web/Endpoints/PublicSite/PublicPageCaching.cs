using System.Globalization;
using CarRental.Web.Components.Public;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>
/// HTTP caching of the public pages marked <see cref="CacheablePublicPageAttribute"/> (ADR-0036).
/// </summary>
/// <remarks>
/// With the interactive render mode configured for <c>/admin</c>, Blazor persists an antiforgery token in every
/// statically rendered page; generating it writes the antiforgery cookie and <c>Cache-Control: no-cache, no-store</c>,
/// so no public page could be cached. For the marked pages only, <see cref="CacheablePageAntiforgery"/> answers that
/// there is no token, and <see cref="Apply"/> sets the public cache policy. Pages with a POST form (results, booking
/// funnel) are not marked and keep the antiforgery cookie and <c>no-store</c>.
/// </remarks>
public static class PublicPageCaching
{
    /// <summary>The marker of the current endpoint, for a GET or HEAD request; <see langword="null"/> otherwise.</summary>
    public static CacheablePublicPageAttribute? CacheableEndpoint(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
            ? context.GetEndpoint()?.Metadata.GetMetadata<CacheablePublicPageAttribute>()
            : null;
    }

    /// <summary>
    /// Sets <c>Cache-Control: public, max-age=…</c> on the response of a marked page when it is a 200 for an anonymous
    /// visitor without any cookie being set. Any other response of a marked page (signed-in staff, a cookie being set)
    /// that carries no cache policy gets <c>no-cache, no-store</c>, as the antiforgery token would have given it, so that
    /// a shared cache never keeps it; errors and re-executed pages are left as is.
    /// </summary>
    public static void Apply(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CacheableEndpoint(context) is not { } cacheable || context.Response.HasStarted)
        {
            return;
        }

        var anonymous = context.User.Identity?.IsAuthenticated != true;
        context.Response.OnStarting(
            static state =>
            {
                var (http, maxAge, anonymous) = ((HttpContext, int, bool))state;
                var response = http.Response;
                if (response.StatusCode != StatusCodes.Status200OK)
                {
                    return Task.CompletedTask;
                }

                if (anonymous && !response.Headers.ContainsKey("Set-Cookie"))
                {
                    response.Headers.CacheControl = string.Create(CultureInfo.InvariantCulture, $"public, max-age={maxAge}");
                    response.Headers.Remove("Pragma");
                }
                else if (StringValues.IsNullOrEmpty(response.Headers.CacheControl))
                {
                    response.Headers.CacheControl = "no-cache, no-store";
                    response.Headers.Pragma = "no-cache";
                }

                return Task.CompletedTask;
            },
            (context, cacheable.MaxAgeSeconds, anonymous));
    }

    /// <summary>Wraps the registered <see cref="IAntiforgery"/> with <see cref="CacheablePageAntiforgery"/>.</summary>
    public static IServiceCollection AddCacheablePublicPages(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registration = services.LastOrDefault(d => d.ServiceType == typeof(IAntiforgery))
            ?? throw new InvalidOperationException("Register antiforgery (AddAntiforgery or AddRazorComponents) before the public pages.");
        services.Remove(registration);
        services.AddSingleton<IAntiforgery>(provider => new CacheablePageAntiforgery(
            Resolve(provider, registration),
            provider.GetRequiredService<IOptions<AntiforgeryOptions>>()));
        return services;
    }

    private static IAntiforgery Resolve(IServiceProvider provider, ServiceDescriptor registration) =>
        (IAntiforgery)(registration.ImplementationInstance
                       ?? registration.ImplementationFactory?.Invoke(provider)
                       ?? ActivatorUtilities.CreateInstance(provider, registration.ImplementationType!));
}

/// <summary>
/// Antiforgery that hands out no token on the GET of a cacheable public page (see <see cref="PublicPageCaching"/>) and
/// behaves exactly like the framework everywhere else; validation is never changed.
/// </summary>
internal sealed class CacheablePageAntiforgery(IAntiforgery inner, IOptions<AntiforgeryOptions> options) : IAntiforgery
{
    public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) =>
        PublicPageCaching.CacheableEndpoint(httpContext) is null ? inner.GetAndStoreTokens(httpContext) : NoTokens();

    public AntiforgeryTokenSet GetTokens(HttpContext httpContext) =>
        PublicPageCaching.CacheableEndpoint(httpContext) is null ? inner.GetTokens(httpContext) : NoTokens();

    public Task<bool> IsRequestValidAsync(HttpContext httpContext) => inner.IsRequestValidAsync(httpContext);

    public Task ValidateRequestAsync(HttpContext httpContext) => inner.ValidateRequestAsync(httpContext);

    public void SetCookieTokenAndHeader(HttpContext httpContext) => inner.SetCookieTokenAndHeader(httpContext);

    private AntiforgeryTokenSet NoTokens() => new(null, null, options.Value.FormFieldName, options.Value.HeaderName);
}
