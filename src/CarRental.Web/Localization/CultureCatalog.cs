using System.Globalization;
using CarRental.Web.Hosting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Localization;

/// <summary>
/// UI cultures: French (neutral resources, default) and English; Arabic (right-to-left) is prepared and switched on with
/// <c>Localization:EnableArabic</c> once translated. Public URLs are prefixed by the culture (<c>/fr/…</c>, <c>/en/…</c>).
/// </summary>
public sealed class CultureCatalog
{
    public const string Default = "fr";

    public CultureCatalog(IOptions<LocalizationSettings> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Enabled = options.Value.EnableArabic ? ["fr", "en", "ar"] : ["fr", "en"];
        SupportedCultures = Enabled.Select(CultureInfo.GetCultureInfo).ToArray();
    }

    /// <summary>Every culture the platform is built for (resources may be partial for the disabled ones).</summary>
    public static IReadOnlyList<string> Prepared { get; } = ["fr", "en", "ar"];

    public IReadOnlyList<string> Enabled { get; }

    public IReadOnlyList<CultureInfo> SupportedCultures { get; }

    public static string NativeName(string culture) => culture switch
    {
        "fr" => "Français",
        "en" => "English",
        "ar" => "العربية",
        _ => culture,
    };

    public bool IsEnabled(string? culture) =>
        culture is not null && Enabled.Contains(culture, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Culture from the first path segment (<c>/en/…</c>), including for a re-executed status page.</summary>
internal sealed class PathRequestCultureProvider(CultureCatalog catalog) : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var path = httpContext.Features.Get<IStatusCodeReExecuteFeature>()?.OriginalPath ?? httpContext.Request.Path.Value;
        var segment = FirstSegment(path);
        return catalog.IsEnabled(segment)
            ? Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(segment!.ToLowerInvariant()))
            : NullProviderCultureResult;
    }

    internal static string? FirstSegment(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return null;
        }

        var end = path.IndexOf('/', 1);
        var segment = end < 0 ? path[1..] : path[1..end];
        return segment.Length == 0 ? null : segment;
    }
}

/// <summary>Public routes are <c>/{culture:length(2)}/…</c>; a two-letter segment that is not an enabled culture is a 404.</summary>
internal sealed class PublicCultureMiddleware(RequestDelegate next, CultureCatalog catalog)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetRouteValue("culture") is string culture && !catalog.IsEnabled(culture))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return next(context);
    }
}

internal static class CultureEndpoints
{
    public const string SetCulturePath = "/culture/set";

    /// <summary><c>/</c> redirects to the remembered or preferred culture; <c>/culture/set</c> stores the choice in a cookie.</summary>
    public static IEndpointRouteBuilder MapCultureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", (HttpContext context, CultureCatalog catalog) =>
        {
            var requested = context.Features.Get<IRequestCultureFeature>()?.RequestCulture.UICulture.TwoLetterISOLanguageName;
            var culture = catalog.IsEnabled(requested) ? requested! : CultureCatalog.Default;
            context.Response.Headers.Vary = "Accept-Language, Cookie";
            return Results.Redirect($"/{culture}");
        });

        endpoints.MapGet(SetCulturePath, (string culture, string? returnUrl, HttpContext context, CultureCatalog catalog) =>
        {
            if (!catalog.IsEnabled(culture))
            {
                return Results.BadRequest();
            }

            context.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture.ToLowerInvariant())),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps,
                });
            return Results.LocalRedirect(LocalUrls.IsLocal(returnUrl) ? returnUrl! : "/");
        });

        return endpoints;
    }
}

public static class LocalUrls
{
    /// <summary>
    /// Same rule as ASP.NET Core <c>IsLocalUrl</c>: a rooted path that is not protocol-relative and holds no control
    /// character (a browser drops a tab or a line break: <c>/\t/evil.example</c> would become <c>//evil.example</c>, and
    /// <c>LocalRedirect</c> would throw on it).
    /// </summary>
    public static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url)
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.AsSpan().ContainsAnyInRange('\u0000', '\u001f')
        && !url.AsSpan().ContainsAnyInRange('\u007f', '\u009f');
}
