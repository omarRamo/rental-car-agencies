using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Application.Customers.Accounts;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers.Accounts;
using CarRental.Web.Components.Public.Search;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Endpoints.PublicSite;
using CarRental.Web.Hosting;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Endpoints.CustomerApi;

/// <summary>
/// The bookings of the authenticated customer: the list and the file (through the services of "Ma réservation", as a
/// system actor of the agency), the request itself (through the booking funnel: quote locked, extras, drivers checked
/// and registered, request submitted under the availability lock, guarantee started), the photos of the documents, the
/// balance and the cancellation. A booking is only reachable when it belongs to the account (made with it, or a booking
/// of the person the account is linked to).
/// </summary>
public static partial class CustomerApiEndpoints
{
    internal static async Task<IResult> BookingsAsync(HttpContext context, PublicAgencyDirectory directory, CancellationToken cancellationToken)
    {
        var customer = CustomerApiAuthentication.Require(context);
        var agency = await directory.GetAsync(PublicSiteAgency.Require(context), cancellationToken);
        await using var scope = CustomerApiAuthentication.CreateScope(context, customer);
        var items = await scope.ServiceProvider.GetRequiredService<CustomerAccountBookings>().ListAsync(customer.AccountId, cancellationToken);
        return Results.Ok<IReadOnlyList<CustomerApiBookingSummary>>([.. items.Select(item => new CustomerApiBookingSummary(
            item.Booking.Id,
            item.Booking.Number,
            CustomerApiNames.Status(item.Booking.Status),
            CustomerApiNames.Channel(item.Booking.Channel),
            item.CategoryCode,
            agency?.Locations.FirstOrDefault(l => l.Id == item.Booking.PickupLocationId)?.Code,
            agency?.Locations.FirstOrDefault(l => l.Id == item.Booking.ReturnLocationId)?.Code,
            TunisTime.ToTunis(item.Booking.PickupAt),
            TunisTime.ToTunis(item.Booking.ReturnAt),
            item.Booking.TotalInclVat.Amount,
            item.Booking.Deposit.Amount,
            Currency.Tnd.Code,
            item.Booking.CreatedAtUtc))]);
    }

    internal static async Task<IResult> BookingAsync(Guid bookingId, HttpContext context, PublicAgencyDirectory directory, CancellationToken cancellationToken)
    {
        var customer = CustomerApiAuthentication.Require(context);
        var site = PublicSiteAgency.Require(context);
        var agency = await directory.GetAsync(site, cancellationToken);
        await using var scope = CustomerApiAuthentication.CreateScope(context, customer);
        if (!await scope.ServiceProvider.GetRequiredService<CustomerAccountBookings>().OwnsAsync(customer.AccountId, bookingId, cancellationToken))
        {
            return Problem(context, CustomerAccountErrors.BookingNotFound);
        }

        var file = await scope.ServiceProvider.GetRequiredService<MyBookingService>().GetFileAsync(bookingId, cancellationToken);
        if (file.IsFailure)
        {
            return Problem(context, file.Error);
        }

        var view = file.Value;
        var category = agency?.Categories.FirstOrDefault(c => c.Id == view.CategoryId) ?? await scope.ServiceProvider.GetRequiredService<Application.Fleet.Contracts.IFleetQueries>().GetCategoryAsync(view.CategoryId, cancellationToken);
        var pickup = agency?.Locations.FirstOrDefault(l => l.Id == view.PickupLocationId);
        var dropOff = agency?.Locations.FirstOrDefault(l => l.Id == view.ReturnLocationId);
        var basePath = $"/{site.Slug}/api/v1/me/bookings/{bookingId:D}/documents/";
        return Results.Ok(new CustomerApiBookingDetails(
            view.BookingId,
            view.Number,
            CustomerApiNames.Status(view.Status),
            CustomerApiNames.Channel(view.Channel),
            category?.AcrissCode ?? string.Empty,
            category?.Name,
            pickup is null ? null : PublicLocationDto.From(pickup),
            dropOff is null ? null : PublicLocationDto.From(dropOff),
            TunisTime.ToTunis(view.PickupAt),
            TunisTime.ToTunis(view.ReturnAt),
            view.TotalInclVat.Amount,
            view.Deposit.Amount,
            view.Paid.Amount,
            view.Balance.Amount,
            Currency.Tnd.Code,
            view.HoldExpiresAt,
            view.PaymentDueAt,
            view.InformationMessage,
            CustomerApiNames.Snake(view.PaymentState),
            [.. view.Timeline.Select(t => new CustomerApiTimelineEntry(CustomerApiNames.Snake(t.Type), t.OccurredAtUtc))],
            [.. view.Drivers.Select(d => new CustomerApiBookingDriver(
                d.Role,
                d.Name,
                CustomerApiNames.Snake(d.IdentityType),
                [.. d.Slots.Select(s => new CustomerApiSlot(s.Key, CustomerApiNames.Snake(s.Document), CustomerApiNames.Snake(s.Side), s.Received, s.Optional))]))],
            view.CanSendDocuments,
            view.CanProvideInformation,
            view.CanPayBalance,
            view.Cancellation is { } cancellation
                ? new CustomerApiCancellation(cancellation.Fee.Amount, cancellation.FreeUntil, cancellation.FeePercent, cancellation.IsFree, cancellation.RefundEstimate.Amount)
                : null,
            [.. view.Documents.Select(d => new CustomerApiDocument(d.Id, CustomerApiNames.Snake(d.Kind), d.Number, d.IssuedAtUtc, basePath + Uri.EscapeDataString(d.Id)))],
            view.Culture));
    }

    /// <summary>
    /// The request: the search is validated again (never trusted), the category looked up among the active categories
    /// of the agency, the funnel opened (locked quote reused when it matches, else priced and locked), the extras and
    /// the drivers applied (the main driver defaults to the account), the request submitted, then the guarantee started
    /// outside any unit of work. A refused step answers 422 with the field errors or the business code.
    /// </summary>
    internal static async Task<IResult> CreateBookingAsync(
        CustomerApiBookingRequest? request,
        HttpContext context,
        PublicAgencyDirectory directory,
        TimeProvider clock,
        IOptions<PublicSiteOptions> site,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BodyRequired();
        }

        var customer = CustomerApiAuthentication.Require(context);
        var agencySite = PublicSiteAgency.Require(context);
        var agency = await directory.GetAsync(agencySite, cancellationToken);
        if (agency is null)
        {
            return Unavailable();
        }

        var settings = context.RequestServices.GetRequiredService<ISettingsReader>();
        if (!customer.EmailVerified && await settings.GetAsync(CustomerAccountSettings.RequireVerifiedEmailToBook, agency.Agency.Id, cancellationToken))
        {
            return Problem(context, CustomerAccountErrors.EmailNotVerified);
        }

        if (!request.TermsAccepted)
        {
            return Problem(context, CustomerAccountErrors.TermsRequired);
        }

        var query = new PublicQuoteRequest(request.PickupLocation, request.ReturnLocation, request.Pickup, request.Return, request.Category, null).ToQuery() with
        {
            QuoteCode = SearchQuery.Parse([new(SearchParameters.Quote, request.QuoteCode ?? string.Empty)]).QuoteCode,
        };
        var validation = SearchValidator.Validate(query, agency.Locations, clock.GetUtcNow());
        if (validation.Criteria is not { } criteria)
        {
            return Results.ValidationProblem(
                validation.Errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray()),
                title: "The search is invalid.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var category = agency.Categories.FirstOrDefault(c => string.Equals(c.AcrissCode, query.Category?.Value, StringComparison.Ordinal));
        if (category is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["category"] = ["category_unknown"] }, title: "The category is unknown.", statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var culture = CustomerAccount.Cultures.Contains(request.Culture?.ToLowerInvariant() ?? string.Empty, StringComparer.Ordinal) ? request.Culture!.ToLowerInvariant() : customer.Culture;
        await using var scope = CustomerApiAuthentication.CreateScope(context, customer);
        var funnel = scope.ServiceProvider.GetRequiredService<FunnelService>();
        var started = await funnel.StartAsync(
            new FunnelStartRequest(
                agency.Agency.Id,
                culture,
                criteria.Channel,
                new RequestAttributionInfo("mobile-app"),
                category.Id,
                category.AcrissCode,
                criteria.PickupLocation.Id,
                criteria.ReturnLocation.Id,
                criteria.PickupAt,
                criteria.ReturnAt,
                criteria.Query.ToQueryString(),
                query.QuoteCode),
            cancellationToken);
        if (started.IsFailure)
        {
            return Problem(context, started.Error);
        }

        var token = started.Value.Token;
        if (request.Extras is { Count: > 0 } extras)
        {
            var quantities = extras
                .Where(e => !string.IsNullOrWhiteSpace(e.Code))
                .Take(20)
                .GroupBy(e => e.Code!.Trim().ToUpperInvariant(), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Math.Clamp(g.Sum(e => e.Quantity), 0, 99), StringComparer.Ordinal);
            var changed = await funnel.ChangeExtrasAsync(token, quantities, cancellationToken);
            if (changed.IsFailure)
            {
                return Problem(context, changed.Error);
            }
        }

        var consents = request.Consents ?? new CustomerApiConsents(Documents: false);
        var drivers = new FunnelDriversInput(
            (request.MainDriver ?? new CustomerApiDriver()).ToInput(customer),
            request.SecondDriver is not null,
            request.SecondDriver?.ToInput(),
            consents.Documents,
            consents.WhatsApp,
            consents.Marketing);
        var saved = await funnel.SaveDriversAsync(token, drivers, cancellationToken);
        if (saved.IsFailure)
        {
            return Problem(context, saved.Error);
        }

        if (!saved.Value.IsValid)
        {
            return Results.ValidationProblem(
                saved.Value.Errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Code).Distinct().ToArray()),
                title: "The drivers are invalid.",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                extensions: new Dictionary<string, object?> { ["code"] = "booking.funnel.drivers_required" });
        }

        var baseUri = PublicUrls.BaseUri(site.Value.BaseUrl, context.Request);
        var submitted = await funnel.SubmitAsync(token, termsAccepted: true, baseUri.AbsoluteUri, culture, cancellationToken);
        if (submitted.IsFailure)
        {
            return Problem(context, submitted.Error);
        }

        var booking = submitted.Value;
        var summary = await scope.ServiceProvider.GetRequiredService<IBookingsQueries>().GetSummaryAsync(booking.BookingId, cancellationToken);
        await scope.ServiceProvider.GetRequiredService<CustomerAccountBookings>().LinkAsync(customer.AccountId, booking.BookingId, summary?.MainDriverId, cancellationToken);

        var confirmation = PublicBookingPaths.Confirmation(agencySite.Slug, culture, booking.Number);
        string? paymentUrl = null;
        DateTimeOffset? paymentExpires = null;
        if (booking.Guarantee.Mode == FunnelGuaranteeMode.PreAuthorization)
        {
            var guarantee = await funnel.StartGuaranteeAsync(booking.BookingId, culture, confirmation, confirmation, cancellationToken);
            if (guarantee is { IsSuccess: true, Value: { } redirect })
            {
                paymentUrl = redirect.RedirectUrl;
                paymentExpires = redirect.ExpiresAtUtc;
            }
        }

        var created = new CustomerApiBookingCreated(
            booking.BookingId,
            booking.Number,
            CustomerApiNames.Status(summary?.Status ?? BookingStatusInfo.PendingValidation),
            summary?.TotalInclVat.Amount ?? 0m,
            Currency.Tnd.Code,
            new CustomerApiGuarantee(CustomerApiNames.Guarantee(booking.Guarantee.Mode), booking.Guarantee.Amount.Amount),
            paymentUrl,
            paymentExpires,
            PublicUrls.Absolute(baseUri, confirmation).AbsoluteUri);
        return Results.Created($"/{agencySite.Slug}/api/v1/me/bookings/{booking.BookingId:D}", created);
    }

    internal static async Task<IResult> UploadDocumentAsync(Guid bookingId, string slot, HttpContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["multipart_required"] }, title: "Send the photo as a multipart form field named 'file'.");
        }

        var customer = CustomerApiAuthentication.Require(context);
        await using var scope = CustomerApiAuthentication.CreateScope(context, customer);
        if (!await scope.ServiceProvider.GetRequiredService<CustomerAccountBookings>().OwnsAsync(customer.AccountId, bookingId, cancellationToken))
        {
            return Problem(context, CustomerAccountErrors.BookingNotFound);
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            return Problem(context, Domain.Customers.CustomersErrors.ImageEmpty);
        }

        await using var content = file.OpenReadStream();
        var sent = await scope.ServiceProvider.GetRequiredService<MyBookingService>().SendDocumentAsync(bookingId, slot, content, cancellationToken);
        return sent.IsSuccess
            ? Results.Ok(new CustomerApiUploadReceived(sent.Value.Slot, sent.Value.ContentType, sent.Value.Length))
            : Problem(context, sent.Error);
    }

    internal static async Task<IResult> DocumentAsync(Guid bookingId, string documentId, HttpContext context, CancellationToken cancellationToken)
    {
        var customer = CustomerApiAuthentication.Require(context);
        await using var scope = CustomerApiAuthentication.CreateScope(context, customer);
        if (!await scope.ServiceProvider.GetRequiredService<CustomerAccountBookings>().OwnsAsync(customer.AccountId, bookingId, cancellationToken))
        {
            return Problem(context, CustomerAccountErrors.BookingNotFound);
        }

        var document = await scope.ServiceProvider.GetRequiredService<MyBookingService>().OpenDocumentAsync(bookingId, documentId, cancellationToken);
        if (document is null)
        {
            return Results.NotFound();
        }

        // The stream is streamed to the response, never buffered; disposed by the file result.
        return Results.File(document.Content, document.ContentType, document.FileName);
    }

    internal static Task<IResult> InformationProvidedAsync(Guid bookingId, HttpContext context, CancellationToken cancellationToken) =>
        OnOwnBookingAsync(bookingId, context, async (service, _) =>
        {
            var provided = await service.ProvideInformationAsync(bookingId, cancellationToken);
            return provided.IsSuccess ? Results.NoContent() : Problem(context, provided.Error);
        }, cancellationToken);

    internal static Task<IResult> PayBalanceAsync(Guid bookingId, HttpContext context, CancellationToken cancellationToken) =>
        OnOwnBookingAsync(bookingId, context, async (service, customer) =>
        {
            var slug = PublicSiteAgency.SlugOf(context);
            var back = PublicBookingPaths.PaymentResult(slug, customer.Culture);
            var started = await service.StartBalancePaymentAsync(bookingId, customer.Culture, back, back, cancellationToken);
            return started.IsSuccess
                ? Results.Ok(new CustomerApiPaymentRedirect(started.Value.PaymentId, started.Value.RedirectUrl, started.Value.ExpiresAtUtc))
                : Problem(context, started.Error);
        }, cancellationToken);

    internal static async Task<IResult> CancelAsync(Guid bookingId, HttpContext context, CancellationToken cancellationToken)
    {
        var body = context.Request.ContentLength > 0 ? await context.Request.ReadFromJsonAsync<CustomerApiCancelRequest>(cancellationToken) : null;
        return await OnOwnBookingAsync(bookingId, context, async (service, _) =>
        {
            var file = await service.GetFileAsync(bookingId, cancellationToken);
            if (file.IsFailure || file.Value.Cancellation is not { } cancellation)
            {
                return Problem(context, Domain.Bookings.Access.AccessErrors.CancellationNotAllowed);
            }

            if (body?.ExpectedFee is { } expected && expected != cancellation.Fee.Amount)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "The cancellation fee changed: read the booking again.",
                    extensions: new Dictionary<string, object?> { ["code"] = "booking.access.fee_changed", ["fee"] = cancellation.Fee.Amount });
            }

            var cancelled = await service.CancelAsync(bookingId, cancellationToken);
            return cancelled.IsSuccess
                ? Results.Ok(new CustomerApiCancelled(cancelled.Value.Fee.Amount, cancelled.Value.RefundAmount.Amount, CustomerApiNames.Snake(cancelled.Value.Refund)))
                : Problem(context, cancelled.Error);
        }, cancellationToken);
    }

    private static async Task<IResult> OnOwnBookingAsync(Guid bookingId, HttpContext context, Func<MyBookingService, CustomerIdentity, Task<IResult>> action, CancellationToken cancellationToken)
    {
        var customer = CustomerApiAuthentication.Require(context);
        await using var scope = CustomerApiAuthentication.CreateScope(context, customer);
        if (!await scope.ServiceProvider.GetRequiredService<CustomerAccountBookings>().OwnsAsync(customer.AccountId, bookingId, cancellationToken))
        {
            return Problem(context, CustomerAccountErrors.BookingNotFound);
        }

        return await action(scope.ServiceProvider.GetRequiredService<MyBookingService>(), customer);
    }
}
