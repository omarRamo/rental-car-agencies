using System.Globalization;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Common;
using CarRental.Web.Components.Public.Booking;
using CarRental.Web.Resources;
using CarRental.Web.Resources.Public;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Localization;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>
/// The forms of the booking funnel (ADR-0049, ADR-0050): step 1 options, step 2 drivers, the photos of the documents,
/// step 3 submission, and a new attempt of the guarantee from the confirmation. Each form is checked (antiforgery, rate
/// limit, Turnstile for the submission), handled by <see cref="FunnelService"/> in a scope of the system actor
/// <see cref="PublicActors.Funnel"/> once the token of the address was checked, and ends with a 303 to a page.
/// </summary>
/// <remarks>
/// The guarantee is started outside any unit of work, after the request was committed; the browser is then sent to a
/// page of the site that leads to the payment page of the bank (a 302 to the bank after a POST would break the CSP
/// <c>form-action 'self'</c>).
/// </remarks>
public static partial class BookingFunnelEndpoints
{
    /// <summary>Action of the Turnstile widget of the submission.</summary>
    public const string SubmitAction = "booking_submit";

    /// <summary>Form field names of step 2: <c>{driver}.{field}</c> (<c>main.birth_date</c>) plus these.</summary>
    public const string SecondDriverField = "second.enabled";

    public const string LatinScriptSuffix = "license_latin";

    public const string DocumentConsentField = "consent.documents";

    public const string WhatsAppConsentField = "consent.whatsapp";

    public const string MarketingConsentField = "consent.marketing";

    public const string TermsField = "terms";

    /// <summary>Prefix of the fields of step 1: <c>extra.{code}</c> = quantity.</summary>
    public const string ExtraFieldPrefix = "extra.";

    /// <summary>Button of step 1 that goes on to step 2 (the other one only updates the price).</summary>
    public const string ContinueAction = "continue";

    /// <summary>Query parameter of a page that shows the error of its last form.</summary>
    public const string ErrorParameter = "erreur";

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "dd-MM-yyyy"];

    public static IEndpointRouteBuilder MapBookingFunnel(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var (french, english) = (BookingRoutes.FunnelPrefixes[0], BookingRoutes.FunnelPrefixes[1]);

        Map(endpoints, $"{french}/{{token}}/{BookingRoutes.Options}", OptionsAsync, PublicRateLimits.Search);
        Map(endpoints, $"{english}/{{token}}/{BookingRoutes.Options}", OptionsAsync, PublicRateLimits.Search);
        Map(endpoints, $"{french}/{{token}}/conducteurs", DriversAsync, PublicRateLimits.Search);
        Map(endpoints, $"{english}/{{token}}/drivers", DriversAsync, PublicRateLimits.Search);
        Map(endpoints, $"{french}/{{token}}/paiement", SubmitAsync, PublicRateLimits.Submit);
        Map(endpoints, $"{english}/{{token}}/payment", SubmitAsync, PublicRateLimits.Submit);
        Map(endpoints, $"{french}/confirmation/{{number}}/paiement", RetryGuaranteeAsync, PublicRateLimits.Submit);
        Map(endpoints, $"{english}/confirmation/{{number}}/payment", RetryGuaranteeAsync, PublicRateLimits.Submit);
        foreach (var prefix in BookingRoutes.FunnelPrefixes)
        {
            Map(endpoints, $"{prefix}/{{token}}/{BookingRoutes.Uploads}", UploadAsync, PublicRateLimits.Upload)
                .WithMetadata(new RequestSizeLimit(PublicForms.UploadRequestLimit))
                .WithFormOptions(multipartBodyLengthLimit: PublicForms.UploadRequestLimit);
        }

        return endpoints;
    }

    /// <summary>A POST form of the funnel: antiforgery, rate limit, anonymous; before the page of the same address.</summary>
    internal static RouteHandlerBuilder Map(IEndpointRouteBuilder endpoints, string pattern, Delegate handler, string rateLimit) =>
        endpoints.MapPost(pattern, handler)
            .WithMetadata(new RequireAntiforgeryTokenAttribute())
            .AllowAnonymous()
            .RequireRateLimiting(rateLimit)
            .WithOrder(-1)
            .ExcludeFromDescription();

    /// <summary>Step 1: the extras chosen (re-quoted and re-locked when they change), then step 1 again or step 2.</summary>
    internal static async Task<IResult> OptionsAsync(string culture, string token, HttpContext context)
    {
        if (await PublicForms.ReadAsync(context) is not { } form)
        {
            return Results.Empty;
        }

        var lang = culture.ToLowerInvariant();
        var quantities = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, value) in form)
        {
            if (key.StartsWith(ExtraFieldPrefix, StringComparison.Ordinal)
                && key.Length is > 6 and <= 64
                && int.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var quantity))
            {
                quantities[key[ExtraFieldPrefix.Length..]] = Math.Clamp(quantity, 0, 99);
            }
        }

        await using var scope = PublicActors.CreateScope(context, PublicActors.Funnel);
        var funnel = scope.ServiceProvider.GetRequiredService<FunnelService>();
        var changed = await funnel.ChangeExtrasAsync(token, quantities, context.RequestAborted);
        if (changed.IsFailure)
        {
            return await FailedAsync(context, funnel, lang, token, FunnelStep.Options, changed.Error);
        }

        var next = PublicForms.Text(form, "action") == ContinueAction ? FunnelStep.Drivers : FunnelStep.Options;
        return PublicForms.SeeOther(context, BookingRoutes.Step(lang, token, next));
    }

    /// <summary>Step 2: the drivers as typed; valid ones lead to step 3, otherwise step 2 shows the errors.</summary>
    internal static async Task<IResult> DriversAsync(string culture, string token, HttpContext context)
    {
        if (await PublicForms.ReadAsync(context) is not { } form)
        {
            return Results.Empty;
        }

        var lang = culture.ToLowerInvariant();
        var hasSecond = PublicForms.Checked(form, SecondDriverField);
        var input = new FunnelDriversInput(
            ReadDriver(form, FunnelFields.Main),
            hasSecond,
            hasSecond ? ReadDriver(form, FunnelFields.Second) : null,
            PublicForms.Checked(form, DocumentConsentField),
            PublicForms.Checked(form, WhatsAppConsentField),
            PublicForms.Checked(form, MarketingConsentField));

        await using var scope = PublicActors.CreateScope(context, PublicActors.Funnel);
        var funnel = scope.ServiceProvider.GetRequiredService<FunnelService>();
        var saved = await funnel.SaveDriversAsync(token, input, context.RequestAborted);
        if (saved.IsFailure)
        {
            return await FailedAsync(context, funnel, lang, token, FunnelStep.Drivers, saved.Error);
        }

        return saved.Value.IsValid
            ? PublicForms.SeeOther(context, BookingRoutes.Step(lang, token, FunnelStep.Payment))
            : PublicForms.SeeOther(context, BookingRoutes.Step(lang, token, FunnelStep.Drivers));
    }

    /// <summary>
    /// A photo of a document for a slot of the session (called by the upload script, which asks for JSON): the outcome
    /// and a text to show, never the image or an address of it.
    /// </summary>
    internal static async Task<IResult> UploadAsync(string culture, string token, HttpContext context)
    {
        var lang = culture.ToLowerInvariant();
        var json = PublicForms.WantsJson(context.Request);
        if (!json)
        {
            if (await PublicForms.ReadAsync(context) is null)
            {
                return Results.Empty;
            }
        }
        else if (context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false } || !context.Request.HasFormContentType)
        {
            return UploadOutcome(context, null, BookingErrorKeys.FormExpired);
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var slot = PublicForms.Text(form, "slot");
        var file = form.Files.GetFile("file");
        Result<FunnelUploadResult> uploaded;
        if (file is null || file.Length == 0)
        {
            uploaded = CarRental.Domain.Customers.CustomersErrors.ImageEmpty;
        }
        else
        {
            await using var scope = PublicActors.CreateScope(context, PublicActors.Funnel);
            var funnel = scope.ServiceProvider.GetRequiredService<FunnelService>();
            await using var content = file.OpenReadStream();
            uploaded = await funnel.UploadAsync(token, slot, content, context.RequestAborted);
        }

        if (!json)
        {
            return PublicForms.SeeOther(context, BookingRoutes.Step(lang, token, FunnelStep.Drivers));
        }

        return uploaded.IsSuccess
            ? UploadOutcome(context, uploaded.Value.Slot, null)
            : UploadOutcome(context, slot, PublicForms.VisibleCode(uploaded.Error));
    }

    /// <summary>
    /// Step 3: the request (Turnstile <c>booking_submit</c>, terms accepted), then its guarantee: the page leading to the
    /// payment page of the bank, or the confirmation (payment link mode, or the guarantee could not start).
    /// </summary>
    internal static async Task<IResult> SubmitAsync(string culture, string token, HttpContext context, PublicBookingCookies cookies)
    {
        if (await PublicForms.ReadAsync(context) is not { } form)
        {
            return Results.Empty;
        }

        var lang = culture.ToLowerInvariant();
        var paymentStep = BookingRoutes.Step(lang, token, FunnelStep.Payment);
        if (!await PublicForms.IsHumanAsync(context, form, SubmitAction))
        {
            return PublicForms.SeeOther(context, WithError(paymentStep, FunnelErrors.VerificationFailed.Code));
        }

        await using var scope = PublicActors.CreateScope(context, PublicActors.Funnel);
        var funnel = scope.ServiceProvider.GetRequiredService<FunnelService>();
        var submitted = await funnel.SubmitAsync(token, PublicForms.Checked(form, TermsField), PublicActors.BaseUrl(context), lang, context.RequestAborted);
        if (submitted.IsFailure)
        {
            return await FailedAsync(context, funnel, lang, token, FunnelStep.Payment, submitted.Error);
        }

        var request = submitted.Value;
        cookies.SetRequest(context, request.BookingId);
        return request.Guarantee.Mode == FunnelGuaranteeMode.PreAuthorization
            ? await GuaranteeAsync(context, funnel, lang, request.BookingId, request.Number)
            : PublicForms.SeeOther(context, BookingRoutes.Confirmation(lang, request.Number));
    }

    /// <summary>A new attempt of the guarantee of the request sent from this browser (failed or expired payment page).</summary>
    internal static async Task<IResult> RetryGuaranteeAsync(string culture, string number, HttpContext context, PublicBookingCookies cookies)
    {
        if (await PublicForms.ReadAsync(context) is null)
        {
            return Results.Empty;
        }

        var lang = culture.ToLowerInvariant();
        var page = BookingRoutes.Confirmation(lang, number);
        if (cookies.ReadRequest(context) is not { } bookingId)
        {
            return PublicForms.SeeOther(context, page);
        }

        await using var scope = PublicActors.CreateScope(context, PublicActors.Funnel);
        var funnel = scope.ServiceProvider.GetRequiredService<FunnelService>();
        var confirmation = await funnel.GetConfirmationAsync(bookingId, context.RequestAborted);
        if (confirmation is null || !string.Equals(confirmation.Number, number, StringComparison.Ordinal) || !confirmation.CanRetryPayment)
        {
            return PublicForms.SeeOther(context, page);
        }

        return await GuaranteeAsync(context, funnel, lang, bookingId, confirmation.Number);
    }

    /// <summary>The pre-authorization of the request (outside any unit of work): the page leading to the bank, or the confirmation.</summary>
    private static async Task<IResult> GuaranteeAsync(HttpContext context, FunnelService funnel, string lang, Guid bookingId, string number)
    {
        var confirmation = BookingRoutes.Confirmation(lang, number);
        var started = await funnel.StartGuaranteeAsync(bookingId, lang, confirmation, confirmation, context.RequestAborted);
        if (started.IsFailure || started.Value is not { } redirect)
        {
            // The confirmation shows that the request is not guaranteed yet, with a new attempt.
            return PublicForms.SeeOther(context, confirmation);
        }

        return PublicForms.SeeOther(context, BookingRoutes.PaymentRedirect(lang, redirect.PaymentId));
    }

    /// <summary>
    /// A refused form: the search (session unknown or expired), the confirmation (already sent), or the same step with
    /// the error to show.
    /// </summary>
    private static async Task<IResult> FailedAsync(HttpContext context, FunnelService funnel, string lang, string token, FunnelStep step, Error error)
    {
        var fallback = await BookingSelectionEndpoint.FallbackForAsync(lang, token, error.Code, funnel, context.RequestAborted);
        return PublicForms.SeeOther(context, fallback ?? WithError(BookingRoutes.Step(lang, token, step), PublicForms.VisibleCode(error)));
    }

    internal static string WithError(string path, string code) => QueryHelpersAdd(path, ErrorParameter, code);

    private static string QueryHelpersAdd(string path, string name, string value) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(path, name, value);

    /// <summary>The JSON answer of an upload: <c>{ slot, received, error, message }</c>.</summary>
    internal static IResult UploadOutcome(HttpContext context, string? slot, string? errorCode)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (errorCode is null)
        {
            var texts = context.RequestServices.GetRequiredService<IStringLocalizer<BookingResources>>();
            return Results.Json(new UploadAnswer(slot, true, null, texts["Photo.Received"]));
        }

        return Results.Json(
            new UploadAnswer(slot, false, errorCode, ErrorText(context.RequestServices, errorCode)),
            statusCode: errorCode == BookingErrorKeys.FormExpired ? StatusCodes.Status400BadRequest : StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>The text of an error code shown by a public page (business error texts, or the funnel's own).</summary>
    public static string ErrorText(IServiceProvider services, string code)
    {
        ArgumentNullException.ThrowIfNull(services);
        var texts = services.GetRequiredService<IStringLocalizer<BookingResources>>();
        if (code is PublicForms.GenericError or BookingErrorKeys.FormExpired)
        {
            return texts[$"Error.{code}"];
        }

        var shared = services.GetRequiredService<IStringLocalizer<SharedResources>>()[$"Error.{code}"];
        return shared.ResourceNotFound ? texts[$"Error.{PublicForms.GenericError}"] : shared.Value;
    }

    /// <summary>The fields of a driver of step 2 (dates typed day first are accepted and written as <c>yyyy-MM-dd</c>).</summary>
    internal static FunnelDriverInput ReadDriver(IFormCollection form, string role)
    {
        string? Field(string name) => PublicForms.Text(form, $"{role}.{name}");
        return new FunnelDriverInput(
            Field("first_name"),
            Field("last_name"),
            Field("nationality")?.ToUpperInvariant(),
            IsoDate(Field("birth_date")),
            Field("phone"),
            Field("email"),
            Field("license_number"),
            IsoDate(Field("license_issued_on")),
            IsoDate(Field("license_expires_on")),
            Field("license_country")?.ToUpperInvariant(),
            Field(LatinScriptSuffix) != "0",
            Field("identity_number"),
            Field("identity_country")?.ToUpperInvariant(),
            IsoDate(Field("identity_expires_on")),
            Field("permit_number"),
            Field("permit_country")?.ToUpperInvariant(),
            IsoDate(Field("permit_expires_on")));
    }

    /// <summary>A date as <c>yyyy-MM-dd</c> when it can be read (browsers without a date picker), else the text as typed.</summary>
    internal static string? IsoDate(string? value) =>
        value is not null && DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value;

    /// <summary>The answer to the upload script.</summary>
    internal sealed record UploadAnswer(string? Slot, bool Received, string? Error, string Message);
}

/// <summary>Text keys of the funnel pages that are not business error codes.</summary>
public static class BookingErrorKeys
{
    /// <summary>The antiforgery token of an upload is missing or expired (page left open too long).</summary>
    public const string FormExpired = "public.form_expired";
}
