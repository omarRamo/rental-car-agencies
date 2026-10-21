using System.Globalization;
using System.Text.Encodings.Web;
using CarRental.Web.Localization;
using CarRental.Web.Resources.Public;
using Microsoft.Extensions.Localization;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>
/// A short localized error page written directly by an endpoint or a middleware (429, refused form). Writing the body
/// keeps the status code pages from re-executing the request, which a POST with an invalid antiforgery token would make
/// fail again. Never cached.
/// </summary>
public static class PublicErrorPage
{
    public static Task WriteAsync(HttpContext context, int statusCode, string titleKey, string messageKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var localizer = context.RequestServices.GetRequiredService<IStringLocalizer<PublicResources>>();
        var culture = CultureInfo.CurrentUICulture;
        var language = culture.TwoLetterISOLanguageName;
        var home = context.RequestServices.GetRequiredService<CultureCatalog>().IsEnabled(language) ? $"/{language}" : "/";
        var encoder = HtmlEncoder.Default;
        var title = encoder.Encode(localizer[titleKey]);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(
            $"""
            <!DOCTYPE html>
            <html lang="{encoder.Encode(language)}" dir="{(culture.TextInfo.IsRightToLeft ? "rtl" : "ltr")}">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="robots" content="noindex"><title>{title}</title><link rel="stylesheet" href="/css/site.css"></head>
            <body><main class="site-main container"><section class="status-page"><h1>{title}</h1><p>{encoder.Encode(localizer[messageKey])}</p><p><a href="{home}">{encoder.Encode(localizer["TooManyRequests.Home"])}</a></p></section></main></body>
            </html>
            """,
            cancellationToken);
    }
}
