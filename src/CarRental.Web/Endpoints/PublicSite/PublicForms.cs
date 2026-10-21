using CarRental.Domain.Common;
using CarRental.Web.Endpoints.PublicSite.Turnstile;
using CarRental.Web.Hosting;
using CarRental.Web.Localization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Primitives;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>
/// The plain HTML forms of the booking funnel and of "Ma réservation" (no JavaScript needed, ADR-0036): the antiforgery
/// token is checked before the form is read (a refused form gets a short page, never an exception), every action ends
/// with a 303 to a page of the site (post/redirect/get), and nothing posted is trusted beyond the choices it expresses.
/// </summary>
public static class PublicForms
{
    /// <summary>Longest photo accepted (10 MB) plus the multipart envelope.</summary>
    public const long UploadRequestLimit = (10 * 1024 * 1024) + (256 * 1024);

    /// <summary>Longest value read from a text field of a public form.</summary>
    public const int MaxFieldLength = 200;

    /// <summary>
    /// The posted form, or <see langword="null"/> when its antiforgery token is invalid or missing: the 400 page "form
    /// expired" is then already written (reading the form would throw).
    /// </summary>
    public static async Task<IFormCollection?> ReadAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false } || !context.Request.HasFormContentType)
        {
            await PublicErrorPage.WriteAsync(context, StatusCodes.Status400BadRequest, "FormExpired.Title", "FormExpired.Message", context.RequestAborted);
            return null;
        }

        return await context.Request.ReadFormAsync(context.RequestAborted);
    }

    /// <summary>303 See Other to a local path of the site (the end of every public form).</summary>
    public static IResult SeeOther(HttpContext context, string localPath)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Location = LocalUrls.IsLocal(localPath) ? localPath : "/";
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    /// <summary>The first value of a field, trimmed and cut to <see cref="MaxFieldLength"/>; <see langword="null"/> when empty.</summary>
    public static string? Text(IFormCollection form, string name)
    {
        ArgumentNullException.ThrowIfNull(form);
        return Text(form[name]);
    }

    public static string? Text(StringValues values)
    {
        var value = values.Count > 0 ? values[0]?.Trim() : null;
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length > MaxFieldLength ? value[..MaxFieldLength] : value;
    }

    /// <summary>A ticked checkbox (<c>1</c>, <c>on</c> or <c>true</c>).</summary>
    public static bool Checked(IFormCollection form, string name)
    {
        ArgumentNullException.ThrowIfNull(form);
        return Text(form, name) is "1" or "on" or "true";
    }

    /// <summary>
    /// The anti-robot check of a form (Turnstile, ADR-0037) for <paramref name="action"/>, solved on the site that
    /// receives the form (host names filtered by <c>HostFiltering</c>).
    /// </summary>
    public static async Task<bool> IsHumanAsync(HttpContext context, IFormCollection form, string action)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(form);
        var verifier = context.RequestServices.GetRequiredService<ITurnstileVerifier>();
        var ip = context.RequestServices.GetRequiredService<ClientIpResolver>().Resolve(context);
        var verification = await verifier.VerifyAsync(
            form[TurnstileOptions.ResponseFieldName].ToString(),
            ip,
            action,
            context.Request.Host.Host,
            context.RequestAborted);
        return verification.Success;
    }

    /// <summary>Whether the caller is the upload script (it asks for JSON) rather than a plain form.</summary>
    public static bool WantsJson(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>The code an error may show to a visitor (known texts), else the generic one: nothing about a person or a list leaks.</summary>
    public static string VisibleCode(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return IsVisible(error.Code) ? error.Code : GenericError;
    }

    /// <summary>Text key of an unexpected failure of a public form.</summary>
    public const string GenericError = "public.unexpected";

    /// <summary>The error codes a visitor may see as such (format of what was typed, availability, expiry, files).</summary>
    public static bool IsVisible(string? code) =>
        code is not null
        && (code.StartsWith("booking.funnel.", StringComparison.Ordinal)
            || code.StartsWith("booking.access.", StringComparison.Ordinal)
            || code.StartsWith("booking.quote.", StringComparison.Ordinal)
            || code.StartsWith("customers.document_image.", StringComparison.Ordinal)
            || code.StartsWith("customers.staged_image.", StringComparison.Ordinal)
            || code is "availability.not_available" or "booking.drivers_identical" or "booking.pickup_in_past" or "booking.period_invalid"
                or "booking.hold_expired" or "booking.invalid_transition" or "booking.concurrent_update" or "payments.gateway.disabled"
                or "customers.phone_invalid" or "customers.email_invalid" or "pricing.minimum_duration_not_met");
}

/// <summary>Request size limit of an endpoint (the photo uploads), read by the server for this endpoint only.</summary>
internal sealed class RequestSizeLimit(long maxRequestBodySize) : IRequestSizeLimitMetadata
{
    public long? MaxRequestBodySize { get; } = maxRequestBodySize;
}
