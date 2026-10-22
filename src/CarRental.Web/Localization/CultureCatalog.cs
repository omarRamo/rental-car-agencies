using System.Globalization;
using CarRental.Domain.Referential;
using CarRental.Web.Hosting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Localization;

/// <summary>
/// UI cultures: French (neutral resources, default) and English; Arabic (right-to-left) is prepared and switched on with
/// <c>Localization:EnableArabic</c> once translated. Public URLs carry the culture after the address of the agency's site
/// (<c>/agence-1/fr/…</c>, <c>/agence-1/en/…</c>, ADR-0015 and ADR-0067).
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

/// <summary>
/// Culture from the path of a public address, <c>/{agency}/{culture}/…</c> (<c>/agence-1/en/…</c>): the second segment
/// when the first is not a word of the platform (<c>/admin</c>, <c>/account</c>…), including for a re-executed status page.
/// </summary>
internal sealed class PathRequestCultureProvider(CultureCatalog catalog) : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var path = httpContext.Features.Get<IStatusCodeReExecuteFeature>()?.OriginalPath ?? httpContext.Request.Path.Value;
        var segment = CultureSegment(path);
        return catalog.IsEnabled(segment)
            ? Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(segment!.ToLowerInvariant()))
            : NullProviderCultureResult;
    }

    /// <summary>The segment of <paramref name="path"/> that holds the culture of a public address, or <see langword="null"/>.</summary>
    internal static string? CultureSegment(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return null;
        }

        var segments = path.Split('/', 4, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && !AgencySlug.Reserved.Contains(segments[0].ToLowerInvariant()) ? segments[1] : null;
    }
}

/// <summary>Public routes are <c>/{agency}/{culture:length(2)}/…</c>; a two-letter segment that is not an enabled culture is a 404.</summary>
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

    /// <summary>
    /// <c>/culture/set</c> stores the choice in a cookie (back office, account pages and the page of the platform at
    /// <c>/</c>; the address of a site page carries its culture).
    /// </summary>
    public static IEndpointRouteBuilder MapCultureEndpoints(this IEndpointRouteBuilder endpoints)
    {
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
