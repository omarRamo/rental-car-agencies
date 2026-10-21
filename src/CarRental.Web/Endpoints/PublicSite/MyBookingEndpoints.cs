using System.Globalization;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Common;
using CarRental.Web.Components.Public.Booking;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>
/// The forms of "Ma réservation" (ADR-0051): the access request (e-mail and booking number, Turnstile, same answer
/// whether a booking matches or not), the six-digit code, the single-use link (opened by a click, never by the GET that
/// link scanners make), then the actions of the booking of the session — photos of the documents, "information
/// provided", the balance payment, the cancellation under the policy, the sign out and the documents.
/// </summary>
/// <remarks>
/// The session is a Data Protection cookie (<see cref="PublicBookingCookies.MyBookingCookie"/>) naming a grant and its
/// booking; every request checks the grant again on the server (revoked, expired, other booking) and slides it.
/// Writes run as the system actor <see cref="PublicActors.MyBooking"/>, for the booking of the session only.
/// </remarks>
public static partial class MyBookingEndpoints
{
    /// <summary>Action of the Turnstile widget of the access request.</summary>
    public const string AccessAction = "my_booking_access";

    public const string EmailField = "email";

    public const string NumberField = "numero";

    public const string CodeField = "code";

    /// <summary>The access request a code is typed for (query of the code page, field of its form).</summary>
    public const string RequestParameter = "demande";

    /// <summary>The section the link leads to (<c>pieces</c>: the documents).</summary>
    public const string SectionParameter = "section";

    /// <summary>A notice shown by the page of the booking after an action.</summary>
    public const string NoticeParameter = "info";

    /// <summary>The cancellation fee shown on the cancellation page, posted back to check it did not change.</summary>
    public const string FeeField = "frais";

    /// <summary>Notices of the page of the booking.</summary>
    public static class Notices
    {
        public const string DocumentReceived = "piece";
        public const string InformationSent = "informations";
        public const string Cancelled = "annulee";
        public const string SignedOut = "deconnexion";
        public const string FeeChanged = "frais";
    }

    public static IEndpointRouteBuilder MapMyBooking(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        foreach (var prefix in BookingRoutes.MyBookingPrefixes)
        {
            BookingFunnelEndpoints.Map(endpoints, prefix, RequestAccessAsync, PublicRateLimits.Submit);
            BookingFunnelEndpoints.Map(endpoints, $"{prefix}/{CodeField}", CodeAsync, PublicRateLimits.Submit);
        }

        BookingFunnelEndpoints.Map(endpoints, $"{BookingRoutes.MyBookingPrefixes[0]}/acces/{{token}}", LinkAsync, PublicRateLimits.Submit);
        BookingFunnelEndpoints.Map(endpoints, $"{BookingRoutes.MyBookingPrefixes[1]}/access/{{token}}", LinkAsync, PublicRateLimits.Submit);
        BookingFunnelEndpoints.Map(endpoints, $"{BookingRoutes.MyBookingFilePrefixes[0]}/annulation", CancelAsync, PublicRateLimits.Submit);
        BookingFunnelEndpoints.Map(endpoints, $"{BookingRoutes.MyBookingFilePrefixes[1]}/cancellation", CancelAsync, PublicRateLimits.Submit);
        foreach (var prefix in BookingRoutes.MyBookingFilePrefixes)
        {
            BookingFunnelEndpoints.Map(endpoints, $"{prefix}/{BookingRoutes.Actions.Documents}", UploadAsync, PublicRateLimits.Upload)
                .WithMetadata(new RequestSizeLimit(PublicForms.UploadRequestLimit))
                .WithFormOptions(multipartBodyLengthLimit: PublicForms.UploadRequestLimit);
            BookingFunnelEndpoints.Map(endpoints, $"{prefix}/{BookingRoutes.Actions.InformationProvided}", InformationAsync, PublicRateLimits.Search);
            BookingFunnelEndpoints.Map(endpoints, $"{prefix}/{BookingRoutes.Actions.PayBalance}", PayAsync, PublicRateLimits.Submit);
            BookingFunnelEndpoints.Map(endpoints, $"{prefix}/{BookingRoutes.Actions.SignOut}", SignOutAsync, PublicRateLimits.Search);
            endpoints.MapGet($"{prefix}/documents/{{documentId}}", DocumentAsync)
                .AllowAnonymous()
                .RequireRateLimiting(PublicRateLimits.Search)
                .ExcludeFromDescription();
        }

        return endpoints;
    }

    /// <summary>
    /// The access request: when the number and the e-mail match a booking, a link and a code are e-mailed; the browser
    /// goes to the code page in every case.
    /// </summary>
    internal static async Task<IResult> RequestAccessAsync(string culture, HttpContext context)
    {
        if (await PublicForms.ReadAsync(context) is not { } form)
        {
            return Results.Empty;
        }

        var lang = culture.ToLowerInvariant();
        var page = BookingRoutes.MyBooking(lang);
        if (!await PublicForms.IsHumanAsync(context, form, AccessAction))
        {
            return PublicForms.SeeOther(context, BookingFunnelEndpoints.WithError(page, FunnelErrors.VerificationFailed.Code));
        }

        await using var scope = PublicActors.CreateScope(context, PublicActors.MyBooking);
        var service = scope.ServiceProvider.GetRequiredService<MyBookingService>();
        var outcome = await service.RequestAccessAsync(
            PublicForms.Text(form, EmailField),
            PublicForms.Text(form, NumberField),
            lang,
            PublicActors.BaseUrl(context),
            context.RequestAborted);
        if (!outcome.IsAccepted)
        {
            var problems = string.Join('-', outcome.Problems.Select(p => p == AccessRequestProblem.EmailInvalid ? EmailField : NumberField));
            return PublicForms.SeeOther(context, BookingFunnelEndpoints.WithError(page, problems));
        }

        return PublicForms.SeeOther(context, BookingRoutes.MyBookingCode(lang, outcome.RequestId!.Value));
    }

    /// <summary>The code of an access request: the session of its booking, or the code page again with the same error.</summary>
    internal static async Task<IResult> CodeAsync(string culture, HttpContext context, PublicBookingCookies cookies)
    {
        if (await PublicForms.ReadAsync(context) is not { } form)
        {
            return Results.Empty;
        }

        var lang = culture.ToLowerInvariant();
        if (!Guid.TryParse(PublicForms.Text(form, RequestParameter), out var requestId))
        {
            return PublicForms.SeeOther(context, BookingRoutes.MyBooking(lang));
        }

        await using var scope = PublicActors.CreateScope(context, PublicActors.MyBooking);
        var service = scope.ServiceProvider.GetRequiredService<MyBookingService>();
        var opened = await service.OpenWithCodeAsync(requestId, PublicForms.Text(form, CodeField), context.RequestAborted);
        if (opened.IsFailure)
        {
            var code = opened.Error.Code == AccessErrors.AccessLocked.Code ? AccessErrors.AccessLocked.Code : AccessErrors.AccessInvalid.Code;
            return PublicForms.SeeOther(context, BookingFunnelEndpoints.WithError(BookingRoutes.MyBookingCode(lang, requestId), code));
        }

        return Open(context, cookies, opened.Value, lang, documents: false);
    }

    /// <summary>The link of an e-mail, confirmed by a click on its page: the session of its booking, once.</summary>
    internal static async Task<IResult> LinkAsync(string culture, string token, HttpContext context, PublicBookingCookies cookies)
    {
        if (await PublicForms.ReadAsync(context) is not { } form)
        {
            return Results.Empty;
        }

        var lang = culture.ToLowerInvariant();
        await using var scope = PublicActors.CreateScope(context, PublicActors.MyBooking);
        var service = scope.ServiceProvider.GetRequiredService<MyBookingService>();
        var opened = await service.OpenWithLinkAsync(token, context.RequestAborted);
        if (opened.IsFailure)
        {
            return PublicForms.SeeOther(context, BookingFunnelEndpoints.WithError(BookingRoutes.MyBooking(lang), AccessErrors.AccessInvalid.Code));
        }

        var documents = PublicForms.Text(form, SectionParameter) == PublicBookingPaths.DocumentsSection || opened.Value.Purpose == AccessPurpose.CompleteFile;
        return Open(context, cookies, opened.Value, lang, documents);
    }

    /// <summary>A photo of a document of a driver of the booking (script: JSON; plain form: back to the documents).</summary>
    internal static async Task<IResult> UploadAsync(string culture, HttpContext context, PublicBookingCookies cookies)
    {
        var lang = culture.ToLowerInvariant();
        var json = PublicForms.WantsJson(context.Request);
        if (context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false } || !context.Request.HasFormContentType)
        {
            if (json)
            {
                return BookingFunnelEndpoints.UploadOutcome(context, null, BookingErrorKeys.FormExpired);
            }

            await PublicErrorPage.WriteAsync(context, StatusCodes.Status400BadRequest, "FormExpired.Title", "FormExpired.Message", context.RequestAborted);
            return Results.Empty;
        }

        await using var scope = PublicActors.CreateScope(context, PublicActors.MyBooking);
        var service = scope.ServiceProvider.GetRequiredService<MyBookingService>();
        if (await SessionAsync(context, cookies, service) is not { } session)
        {
            return json
                ? BookingFunnelEndpoints.UploadOutcome(context, null, AccessErrors.SessionRequired.Code)
                : PublicForms.SeeOther(context, BookingFunnelEndpoints.WithError(BookingRoutes.MyBooking(lang), AccessErrors.SessionRequired.Code));
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var slot = PublicForms.Text(form, "slot");
        var file = form.Files.GetFile("file");
        Result<FunnelUploadResult> sent;
        if (file is null || file.Length == 0)
        {
            sent = CarRental.Domain.Customers.CustomersErrors.ImageEmpty;
        }
        else
        {
            await using var content = file.OpenReadStream();
            sent = await service.SendDocumentAsync(session.BookingId, slot, content, context.RequestAborted);
        }

        if (json)
        {
            return sent.IsSuccess
                ? BookingFunnelEndpoints.UploadOutcome(context, sent.Value.Slot, null)
                : BookingFunnelEndpoints.UploadOutcome(context, slot, PublicForms.VisibleCode(sent.Error));
        }

        var file2 = BookingRoutes.MyBookingFile(lang);
        var next = sent.IsSuccess
            ? QueryHelpers.AddQueryString(file2, NoticeParameter, Notices.DocumentReceived)
            : BookingFunnelEndpoints.WithError(file2, PublicForms.VisibleCode(sent.Error));
        return PublicForms.SeeOther(context, $"{next}#{PublicBookingPaths.DocumentsSection}");
    }

    /// <summary>The customer sent what the agent asked for: the request goes back to the agent.</summary>
    internal static Task<IResult> InformationAsync(string culture, HttpContext context, PublicBookingCookies cookies) =>
        ActAsync(culture, context, cookies, async (service, session, lang) =>
        {
            var provided = await service.ProvideInformationAsync(session.BookingId, context.RequestAborted);
            return provided.IsSuccess ? Notice(lang, Notices.InformationSent) : Failed(lang, provided.Error);
        });

    /// <summary>The balance, paid online: the page leading to the payment page of the bank.</summary>
    internal static Task<IResult> PayAsync(string culture, HttpContext context, PublicBookingCookies cookies) =>
        ActAsync(culture, context, cookies, async (service, session, lang) =>
        {
            var back = QueryHelpers.AddQueryString(BookingRoutes.MyBookingFile(lang), "paiement", PublicBookingPaths.PaymentIdPlaceholder);
            var started = await service.StartBalancePaymentAsync(session.BookingId, lang, back, back, context.RequestAborted);
            return started.IsSuccess ? BookingRoutes.PaymentRedirect(lang, started.Value.PaymentId) : Failed(lang, started.Error);
        });

    /// <summary>The cancellation, at the fee shown on the cancellation page (a different fee shows the page again).</summary>
    internal static Task<IResult> CancelAsync(string culture, HttpContext context, PublicBookingCookies cookies) =>
        ActAsync(culture, context, cookies, async (service, session, lang) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var file = await service.GetFileAsync(session.BookingId, context.RequestAborted);
            var shown = decimal.TryParse(PublicForms.Text(form, FeeField), NumberStyles.Number, CultureInfo.InvariantCulture, out var fee) ? fee : (decimal?)null;
            if (file.IsFailure || file.Value.Cancellation is not { } cancellation)
            {
                return Failed(lang, AccessErrors.CancellationNotAllowed);
            }

            if (shown != cancellation.Fee.Amount)
            {
                return QueryHelpers.AddQueryString(BookingRoutes.MyBookingCancellation(lang), NoticeParameter, Notices.FeeChanged);
            }

            var cancelled = await service.CancelAsync(session.BookingId, context.RequestAborted);
            return cancelled.IsSuccess
                ? QueryHelpers.AddQueryString(Notice(lang, Notices.Cancelled), "remboursement", cancelled.Value.Refund.ToString())
                : BookingFunnelEndpoints.WithError(BookingRoutes.MyBookingCancellation(lang), PublicForms.VisibleCode(cancelled.Error));
        });

    /// <summary>Ends the session of this browser.</summary>
    internal static async Task<IResult> SignOutAsync(string culture, HttpContext context, PublicBookingCookies cookies)
    {
        if (await PublicForms.ReadAsync(context) is null)
        {
            return Results.Empty;
        }

        var lang = culture.ToLowerInvariant();
        if (cookies.ReadSession(context) is { } session)
        {
            await using var scope = PublicActors.CreateScope(context, PublicActors.MyBooking);
            await scope.ServiceProvider.GetRequiredService<MyBookingService>().SignOutAsync(session.GrantId, context.RequestAborted);
        }

        cookies.ClearSession(context);
        return PublicForms.SeeOther(context, QueryHelpers.AddQueryString(BookingRoutes.MyBooking(lang), NoticeParameter, Notices.SignedOut));
    }

    /// <summary>A document of the booking of the session (contract, invoice), never cached.</summary>
    internal static async Task<IResult> DocumentAsync(string culture, string documentId, HttpContext context, PublicBookingCookies cookies)
    {
        context.Response.Headers.CacheControl = "no-store";
        await using var scope = PublicActors.CreateScope(context, PublicActors.MyBooking);
        var service = scope.ServiceProvider.GetRequiredService<MyBookingService>();
        if (await SessionAsync(context, cookies, service) is not { } session)
        {
            return PublicForms.SeeOther(context, BookingRoutes.MyBooking(culture.ToLowerInvariant()));
        }

        var document = await service.OpenDocumentAsync(session.BookingId, documentId, context.RequestAborted);
        return document is null
            ? Results.NotFound()
            : Results.File(document.Content, document.ContentType, document.FileName);
    }

    /// <summary>
    /// The session of this browser, checked on the server and slid (the cookie follows); <see langword="null"/> and the
    /// cookie removed when it is over.
    /// </summary>
    public static async Task<MyBookingTicket?> SessionAsync(HttpContext context, PublicBookingCookies cookies, MyBookingService service)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cookies);
        ArgumentNullException.ThrowIfNull(service);
        if (cookies.ReadSession(context) is not { } session)
        {
            return null;
        }

        var resumed = await service.ResumeAsync(session.GrantId, session.BookingId, context.RequestAborted);
        if (resumed.IsFailure)
        {
            if (!context.Response.HasStarted)
            {
                cookies.ClearSession(context);
            }

            return null;
        }

        if (!context.Response.HasStarted)
        {
            cookies.SetSession(context, resumed.Value.GrantId, resumed.Value.BookingId, resumed.Value.ExpiresAtUtc);
        }

        return resumed.Value;
    }

    /// <summary>An action on the booking of the session: the form checked, the session required, then a 303 to the path returned.</summary>
    private static async Task<IResult> ActAsync(
        string culture,
        HttpContext context,
        PublicBookingCookies cookies,
        Func<MyBookingService, MyBookingTicket, string, Task<string>> action)
    {
        if (await PublicForms.ReadAsync(context) is null)
        {
            return Results.Empty;
        }

        var lang = culture.ToLowerInvariant();
        await using var scope = PublicActors.CreateScope(context, PublicActors.MyBooking);
        var service = scope.ServiceProvider.GetRequiredService<MyBookingService>();
        if (await SessionAsync(context, cookies, service) is not { } session)
        {
            return PublicForms.SeeOther(context, BookingFunnelEndpoints.WithError(BookingRoutes.MyBooking(lang), AccessErrors.SessionRequired.Code));
        }

        return PublicForms.SeeOther(context, await action(service, session, lang));
    }

    private static IResult Open(HttpContext context, PublicBookingCookies cookies, MyBookingTicket ticket, string lang, bool documents)
    {
        cookies.SetSession(context, ticket.GrantId, ticket.BookingId, ticket.ExpiresAtUtc);
        return PublicForms.SeeOther(context, documents ? BookingRoutes.MyBookingDocuments(lang) : BookingRoutes.MyBookingFile(lang));
    }

    private static string Notice(string lang, string notice) => QueryHelpers.AddQueryString(BookingRoutes.MyBookingFile(lang), NoticeParameter, notice);

    private static string Failed(string lang, Error error) => BookingFunnelEndpoints.WithError(BookingRoutes.MyBookingFile(lang), PublicForms.VisibleCode(error));
}
