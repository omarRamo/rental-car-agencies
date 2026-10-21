using System.Text.Json;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QuoteLineKind = CarRental.Domain.Bookings.Quotes.QuoteLineKind;

namespace CarRental.Application.Bookings.PublicAccess;

/// <summary>
/// The public booking funnel (ADR-0049, ADR-0050): a session opened by the choice of a category (locked quote reused
/// from the deep link or created, guaranteed 24 hours), step 1 options (re-quoted and re-locked), step 2 drivers
/// (validated and evaluated on what was typed only; photos staged by the Customers module), step 3 submission (drivers
/// registered without ever changing a person already known, request submitted under the availability lock), then the
/// guarantee (pre-authorization on the payment page of the bank, outside any unit of work).
/// </summary>
/// <remarks>
/// <para>The token of the addresses is the only key to a session: it is checked (shape, hash, expiry) before anything
/// else; the caller runs the writes as a system actor (<c>system:public-funnel</c>), the reads need no permission.</para>
/// <para>Prices only come from the pricing engine through locked quotes; nothing posted by the browser is trusted beyond
/// the choices it expresses (extra codes and quantities offered by the plan, driver details).</para>
/// </remarks>
public sealed partial class FunnelService(
    IAppDbContext db,
    IBookingRequests requests,
    IAvailabilityQueries availability,
    IPricingQueries pricing,
    ICustomersQueries customers,
    ICustomerRegistration registration,
    IOnlinePayments payments,
    ISettingsReader settings,
    TimeProvider clock,
    ILogger<FunnelService> logger)
{
    /// <summary>Photos accepted per session (retries included).</summary>
    public const int MaxUploadsPerSession = 40;

    /// <summary>Quantity offered for a per-day optional extra (child seat, GPS); one for the others.</summary>
    public const int MaxPerDayQuantity = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The choice of a category: checks it is still available (cached availability: the submission re-checks under the
    /// lock), reuses the locked quote of the deep link when it is valid, unexpired and matches the search exactly, otherwise
    /// prices and locks a new one, and opens the session.
    /// </summary>
    public async Task<Result<FunnelStarted>> StartAsync(FunnelStartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rental = Period.Create(request.PickupAt, request.ReturnAt);
        if (rental.IsFailure)
        {
            return BookingErrors.PeriodInvalid;
        }

        var available = await availability.GetCategoryAvailabilityAsync(request.CategoryId, rental.Value, request.Channel, cancellationToken: cancellationToken);
        if (available is not { IsAvailable: true })
        {
            return FunnelErrors.CategoryUnavailable;
        }

        var now = clock.GetUtcNow();
        var quote = await ReusableQuoteAsync(request, now, cancellationToken);
        var reused = quote is not null;
        if (quote is null)
        {
            var created = await requests.CreateLockedQuoteAsync(
                new PriceRequest(
                    request.AgencyId,
                    request.CategoryId,
                    request.PickupLocationId,
                    request.ReturnLocationId,
                    request.PickupAt,
                    request.ReturnAt,
                    request.Channel),
                cancellationToken);
            if (created.IsFailure)
            {
                return created.Error;
            }

            quote = created.Value;
        }

        var extras = BookingPricing.ChoicesOf(quote.Extras);
        if (extras.IsFailure)
        {
            return extras.Error;
        }

        var token = AccessSecrets.NewToken();
        var session = FunnelSession.Start(
            AccessSecrets.HashToken(token),
            new FunnelStart(
                request.AgencyId,
                request.Culture,
                request.Channel,
                AttributionOf(request.Attribution),
                request.CategoryId,
                request.CategoryCode,
                request.PickupLocationId,
                request.ReturnLocationId,
                request.PickupAt,
                request.ReturnAt,
                request.SearchQuery,
                quote.QuoteId,
                quote.Code,
                quote.ExpiresAtUtc,
                extras.Value),
            now);
        if (session.IsFailure)
        {
            return session.Error;
        }

        db.Set<FunnelSession>().Add(session.Value);
        await db.SaveChangesAsync(cancellationToken);
        LogStarted(session.Value.Id, request.CategoryCode, reused);
        return new FunnelStarted(token, session.Value.Id, reused);
    }

    /// <summary>
    /// A step of the funnel is displayed: the session (or why it cannot be shown), the step measured once, the pending
    /// notice taken.
    /// </summary>
    public async Task<FunnelLookup> OpenAsync(string? token, FunnelSteps step = FunnelSteps.None, CancellationToken cancellationToken = default)
    {
        var session = await FindAsync(token, cancellationToken);
        if (session is null)
        {
            return FunnelLookup.NotFound;
        }

        if (session.IsSubmitted)
        {
            return new FunnelLookup(null, FunnelLookupFailure.Submitted, session.Culture, session.SearchQuery, session.BookingNumber);
        }

        if (session.IsExpiredAt(clock.GetUtcNow()))
        {
            return new FunnelLookup(null, FunnelLookupFailure.Expired, session.Culture, session.SearchQuery);
        }

        var firstVisit = step != FunnelSteps.None && session.MarkTracked(step);
        var notice = session.TakeNotice();
        if (firstVisit || notice is not null)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Measurement and notices are best effort: a concurrent change of the session wins.
                firstVisit = false;
            }
        }

        var view = await ViewAsync(session, notice, firstVisit, cancellationToken);
        return view is null ? FunnelLookup.NotFound : new FunnelLookup(view, null, session.Culture);
    }

    /// <summary>
    /// Step 1: the extras chosen (codes and quantities of the selectable extras the plan offers; anything else is
    /// ignored). A change re-prices the rental with the plan in force and locks the new quote for 24 hours (ADR-0049).
    /// </summary>
    public async Task<Result> ChangeExtrasAsync(string? token, IReadOnlyDictionary<string, int> quantities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quantities);
        var opened = await OpenForChangeAsync(token, cancellationToken);
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        var session = opened.Value;
        var offered = await OfferedExtrasAsync(session, cancellationToken);
        var choices = new List<ExtraChoice>();
        foreach (var extra in offered.Where(e => e.Code != FunnelFields.AdditionalDriverExtra))
        {
            var quantity = Math.Clamp(quantities.TryGetValue(extra.Code, out var value) ? value : 0, 0, MaxQuantity(extra));
            if (quantity > 0)
            {
                choices.Add(ExtraChoice.Create(extra.Code, quantity).Value);
            }
        }

        // The additional driver follows the second driver of step 2, never the options.
        choices.AddRange(session.Extras.Where(e => e.Code == FunnelFields.AdditionalDriverExtra));
        var normalized = ExtraChoice.Normalize(choices);
        if (normalized.IsFailure)
        {
            return normalized.Error;
        }

        if (ExtraChoice.Format(normalized.Value) == session.ExtraSelection)
        {
            return Result.Success();
        }

        return await RelockAsync(session, normalized.Value, ReadDrivers(session.DriversJson), cancellationToken);
    }

    /// <summary>
    /// Step 2: the drivers as typed. Every field is checked, then each driver is validated and evaluated by the Customers
    /// module on these values only (age, licence seniority, documents valid until the return, international permit, CIN
    /// for a Tunisian); errors are kept for the next display. Valid drivers are saved with the consents; the second driver
    /// brings the additional driver extra when the plan has one, and the ages of the drivers the young driver supplement:
    /// the price is then re-quoted and re-locked.
    /// </summary>
    public async Task<Result<FunnelDriversOutcome>> SaveDriversAsync(string? token, FunnelDriversInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var opened = await OpenForChangeAsync(token, cancellationToken);
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        var session = opened.Value;
        var errors = await CheckDriversAsync(session, input, cancellationToken);
        var now = clock.GetUtcNow();
        if (errors.Count > 0)
        {
            var draft = session.SaveDraft(JsonSerializer.Serialize(new FunnelDriversDraft(input, errors), Json), now);
            if (draft.IsFailure)
            {
                return draft.Error;
            }

            var saved = await SaveAsync(cancellationToken);
            return saved.IsFailure ? saved.Error : new FunnelDriversOutcome(false, errors);
        }

        var stored = session.SaveDrivers(
            JsonSerializer.Serialize(input, Json),
            input.DocumentProcessingConsent,
            input.WhatsAppNotificationsConsent,
            input.MarketingConsent,
            now);
        if (stored.IsFailure)
        {
            return stored.Error;
        }

        var offered = await OfferedExtrasAsync(session, cancellationToken);
        var extras = session.Extras.Where(e => e.Code != FunnelFields.AdditionalDriverExtra).ToList();
        if (input.HasSecond && offered.Any(e => e.Code == FunnelFields.AdditionalDriverExtra))
        {
            extras.Add(ExtraChoice.Create(FunnelFields.AdditionalDriverExtra, 1).Value);
        }

        var normalized = ExtraChoice.Normalize(extras).Value;
        var quote = await db.Set<Quote>().AsNoTracking().FirstOrDefaultAsync(q => q.Id == session.QuoteId, cancellationToken);
        var agesChanged = quote is null || !SameAges(QuoteSnapshots.ReadInputs(quote.InputsJson)?.DriverAges, BirthDates(input), session.PickupAt);
        if (ExtraChoice.Format(normalized) != session.ExtraSelection || agesChanged)
        {
            var relocked = await RelockAsync(session, normalized, input, cancellationToken);
            return relocked.IsFailure ? relocked.Error : new FunnelDriversOutcome(true, []);
        }

        var result = await SaveAsync(cancellationToken);
        return result.IsFailure ? result.Error : new FunnelDriversOutcome(true, []);
    }

    /// <summary>
    /// A photo of a document (JPEG, PNG or PDF, 10 MB at most, checked on its content by the Customers module) for a slot
    /// of the session; the image is never sent back.
    /// </summary>
    public async Task<Result<FunnelUploadResult>> UploadAsync(string? token, string? slotKey, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var session = await FindAsync(token, cancellationToken, tracked: false);
        if (session is null)
        {
            return FunnelErrors.SessionNotFound;
        }

        var now = clock.GetUtcNow();
        if (session.IsSubmitted)
        {
            return FunnelErrors.AlreadySubmitted;
        }

        if (session.IsExpiredAt(now))
        {
            return FunnelErrors.SessionExpired;
        }

        if (UploadSlot.Parse(slotKey) is not { } slot)
        {
            return FunnelErrors.UploadSlotInvalid;
        }

        if (await db.Set<FunnelUpload>().CountAsync(u => u.SessionId == session.Id, cancellationToken) >= MaxUploadsPerSession)
        {
            return FunnelErrors.UploadLimitReached;
        }

        var staged = await registration.StageDocumentImageAsync(content, cancellationToken);
        if (staged.IsFailure)
        {
            return staged.Error;
        }

        var upload = FunnelUpload.Create(session.Id, slot, staged.Value.Id, staged.Value.ContentType, staged.Value.Length, now);
        db.Set<FunnelUpload>().Add(upload);
        await db.SaveChangesAsync(cancellationToken);
        return new FunnelUploadResult(upload.Id, slot.Key, upload.ContentType, upload.Length);
    }

    /// <summary>
    /// Step 3: the request. The drivers are registered for the new booking (a person already known is never changed:
    /// its submission waits for an agent), then the request is submitted from the locked quote under the availability
    /// lock (<c>PendingValidation</c>, hold of 24 hours). A failure discards what was registered for it.
    /// </summary>
    public async Task<Result<FunnelSubmitted>> SubmitAsync(
        string? token,
        bool termsAccepted,
        string? publicBaseUrl = null,
        string? culture = null,
        CancellationToken cancellationToken = default)
    {
        var opened = await OpenForChangeAsync(token, cancellationToken);
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        if (!termsAccepted)
        {
            return FunnelErrors.TermsRequired;
        }

        var session = opened.Value;
        if (ReadDrivers(session.DriversJson) is not { } input || !session.DocumentProcessingConsent)
        {
            return FunnelErrors.DriversRequired;
        }

        var parsing = new List<FunnelFieldError>();
        var main = FunnelDriverRules.Parse(input.Main, FunnelFields.Main, contactRequired: true, parsing);
        var second = input.HasSecond && input.Second is { } secondInput
            ? FunnelDriverRules.Parse(secondInput, FunnelFields.Second, contactRequired: false, parsing)
            : null;
        if (main is null || parsing.Count > 0)
        {
            return FunnelErrors.DriversRequired;
        }

        var bookingId = Entity.NewId();
        var now = clock.GetUtcNow();
        var uploads = await LatestUploadsAsync(session.Id, cancellationToken);
        var channel = session.Channel == SalesChannel.WhatsApp ? ConsentChannelInfo.WhatsApp : ConsentChannelInfo.Web;

        // The profile (language, WhatsApp opt-in) exists before the request: its first message is in the right language.
        var language = string.IsNullOrWhiteSpace(culture) ? session.Culture : culture.Trim().ToLowerInvariant();
        db.Set<BookingCustomerProfile>().Add(BookingCustomerProfile.Create(bookingId, language, input.WhatsAppNotificationsConsent, now, publicBaseUrl));
        await db.SaveChangesAsync(cancellationToken);

        var drivers = new List<Guid>();
        foreach (var (role, details) in new[] { (FunnelFields.Main, main), (FunnelFields.Second, second) })
        {
            if (details is null)
            {
                continue;
            }

            var isMain = role == FunnelFields.Main;
            var registered = await registration.RegisterSubmittedDriverAsync(
                new SubmittedDriverRequest(
                    bookingId,
                    channel,
                    details,
                    ImagesFor(role, details, uploads),
                    input.DocumentProcessingConsent,
                    isMain && input.WhatsAppNotificationsConsent,
                    isMain && input.MarketingConsent),
                cancellationToken);
            if (registered.IsFailure)
            {
                return await AbandonAsync(bookingId, registered.Error, cancellationToken);
            }

            drivers.Add(registered.Value.PersonId);
        }

        if (drivers.Count == 2 && drivers[0] == drivers[1])
        {
            return await AbandonAsync(bookingId, BookingErrors.DriversIdentical, cancellationToken);
        }

        var submitted = await requests.SubmitRequestAsync(
            new BookingRequestDraft(
                session.Channel,
                LockedQuoteCode: session.QuoteCode,
                Attribution: AttributionInfo(session.Attribution),
                BookingId: bookingId),
            new BookingRequestParties(drivers[0], drivers.Count > 1 ? drivers[1] : null),
            cancellationToken);
        if (submitted.IsFailure)
        {
            return await AbandonAsync(bookingId, submitted.Error, cancellationToken);
        }

        var request = submitted.Value;
        await MarkSubmittedAsync(session.Id, bookingId, request.Number, cancellationToken);
        LogSubmitted(request.Number, session.Id);
        var guarantee = await GuaranteeAsync(session.AgencyId, request.TotalInclVat, cancellationToken);
        return new FunnelSubmitted(bookingId, request.Number, language, guarantee);
    }

    /// <summary>
    /// The guarantee of a request (after the submission, or again from its confirmation page after a failure): a
    /// pre-authorization of the guaranteed amount on the payment page of the bank, never inside a unit of work. Nothing to
    /// do (<see langword="null"/>) when the request is not waiting any more, is already guaranteed, or is in the payment link
    /// mode.
    /// </summary>
    public async Task<Result<OnlinePaymentRedirect?>> StartGuaranteeAsync(
        Guid bookingId,
        string culture,
        string returnPath,
        string failPath,
        CancellationToken cancellationToken = default)
    {
        var booking = await db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        if (booking.Status is not (BookingStatus.PendingValidation or BookingStatus.InformationRequested))
        {
            return Result.Success<OnlinePaymentRedirect?>(null);
        }

        var guarantee = await GuaranteeAsync(booking.AgencyId, booking.TotalInclVat, cancellationToken);
        if (guarantee.Mode != FunnelGuaranteeMode.PreAuthorization)
        {
            return Result.Success<OnlinePaymentRedirect?>(null);
        }

        var existing = await payments.GetBookingOnlinePaymentsAsync(bookingId, cancellationToken);
        if (existing.Any(p => p.Flow == OnlinePaymentFlowInfo.PreAuthorization && p.Status is PaymentStatusInfo.PreAuthorized or PaymentStatusInfo.Captured))
        {
            return Result.Success<OnlinePaymentRedirect?>(null);
        }

        var started = await payments.StartPreAuthorizationAsync(
            new OnlinePaymentRequest(bookingId, booking.AgencyId, guarantee.Amount.Amount, culture, returnPath, failPath, booking.Number),
            cancellationToken);
        if (started.IsFailure)
        {
            LogGuaranteeFailed(booking.Number, started.Error.Code);
            return started.Error;
        }

        return Result.Success<OnlinePaymentRedirect?>(started.Value);
    }

    /// <summary>The confirmation of a request: status, guarantee and where its payment stands (from the bank's confirmed status).</summary>
    public async Task<FunnelConfirmation?> GetConfirmationAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is null)
        {
            return null;
        }

        var culture = await db.Set<BookingCustomerProfile>()
            .AsNoTracking()
            .Where(p => p.BookingId == bookingId)
            .Select(p => p.Culture)
            .FirstOrDefaultAsync(cancellationToken) ?? "fr";
        var guarantee = await GuaranteeAsync(booking.AgencyId, booking.TotalInclVat, cancellationToken);
        var online = (await payments.GetBookingOnlinePaymentsAsync(bookingId, cancellationToken))
            .Where(p => p.Flow == OnlinePaymentFlowInfo.PreAuthorization)
            .ToList();
        var latest = online.Count == 0 ? null : online[^1];
        var state = online.Any(p => p.Status is PaymentStatusInfo.Captured or PaymentStatusInfo.Refunded)
            ? FunnelPaymentState.Captured
            : online.Any(p => p.Status == PaymentStatusInfo.PreAuthorized)
                ? FunnelPaymentState.Held
                : latest switch
                {
                    null => guarantee.Mode == FunnelGuaranteeMode.PreAuthorization ? FunnelPaymentState.Failed : FunnelPaymentState.None,
                    { Status: PaymentStatusInfo.Pending } => FunnelPaymentState.AwaitingCustomer,
                    _ => FunnelPaymentState.Failed,
                };
        var waiting = booking.Status is BookingStatus.PendingValidation or BookingStatus.InformationRequested;
        var canRetry = waiting
                       && guarantee.Mode == FunnelGuaranteeMode.PreAuthorization
                       && (state == FunnelPaymentState.Failed || (state == FunnelPaymentState.AwaitingCustomer && latest?.ExpiresAtUtc <= clock.GetUtcNow()));
        return new FunnelConfirmation(booking.Id, booking.Number, booking.Status.ToInfo(), booking.TotalInclVat, guarantee, state, canRetry, culture);
    }

    /// <summary>The amount pre-authorized for a total: the ratio of the agency, to the millime (away from zero).</summary>
    public static Money GuaranteeAmount(Money total, decimal ratio) =>
        Money.Tnd(Math.Round(total.Amount * Math.Clamp(ratio, 0.01m, 1m), 3, MidpointRounding.AwayFromZero));

    /// <summary>Whether a locked quote can carry the funnel of a search: valid, unexpired and priced for exactly this rental.</summary>
    internal static bool Matches(LockedQuoteInfo quote, FunnelStartRequest request, DateTimeOffset now) =>
        !quote.IsExpired
        && quote.ExpiresAtUtc > now
        && quote.AgencyId == request.AgencyId
        && quote.CategoryId == request.CategoryId
        && quote.Channel == request.Channel
        && quote.PickupLocationId == request.PickupLocationId
        && quote.ReturnLocationId == request.ReturnLocationId
        && quote.PickupAt == request.PickupAt
        && quote.ReturnAt == request.ReturnAt
        && quote.GuaranteedVehicleId is null;

    private static int MaxQuantity(ExtraOffer extra) =>
        extra.Kind == ExtraKindInfo.Optional && extra.Mode == ExtraPricingModeInfo.PerDay && extra.Code != FunnelFields.AdditionalDriverExtra
            ? MaxPerDayQuantity
            : 1;

    private static BookingAttribution AttributionOf(RequestAttributionInfo? source)
    {
        if (source is null)
        {
            return BookingAttribution.None;
        }

        var attribution = BookingAttribution.Create(source.Source, source.UtmSource, source.UtmMedium, source.UtmCampaign, source.UtmTerm, source.UtmContent);
        return attribution.IsSuccess ? attribution.Value : BookingAttribution.None;
    }

    private static FunnelDriversInput? ReadDrivers(string? json) => json is null ? null : JsonSerializer.Deserialize<FunnelDriversInput>(json, Json);

    private static FunnelDriversDraft? ReadDraft(string? json) => json is null ? null : JsonSerializer.Deserialize<FunnelDriversDraft>(json, Json);

    private static List<DateOnly> BirthDates(FunnelDriversInput? input)
    {
        var dates = new List<DateOnly>();
        foreach (var driver in new[] { input?.Main, input?.HasSecond == true ? input.Second : null })
        {
            if (driver?.BirthDate is { } text && DateOnly.TryParseExact(text, "yyyy-MM-dd", out var date))
            {
                dates.Add(date);
            }
        }

        return dates;
    }

    /// <summary>Whether the quote was priced with the ages of these drivers (completed years at the pick-up date, Tunis).</summary>
    private static bool SameAges(IReadOnlyList<int>? quotedAges, IReadOnlyList<DateOnly> birthDates, DateTimeOffset pickupAt)
    {
        var pickupDate = TunisTime.DateOf(pickupAt);
        var ages = birthDates.Select(b => pickupDate.Year - b.Year - (pickupDate < b.AddYears(pickupDate.Year - b.Year) ? 1 : 0)).Order().ToList();
        return (quotedAges ?? []).Order().SequenceEqual(ages);
    }

    private static List<SubmittedDocumentImages> ImagesFor(string role, SubmittedDriverDetails details, IReadOnlyList<FunnelUpload> uploads)
    {
        var driver = role == FunnelFields.Main ? FunnelDriver.Main : FunnelDriver.Second;
        Guid? Staged(FunnelDocument document, FunnelSide side)
        {
            var key = new UploadSlot(driver, document, side).Key;
            return uploads.LastOrDefault(u => u.Slot == key)?.StagedImageId;
        }

        var images = new List<SubmittedDocumentImages>();
        foreach (var document in details.Documents)
        {
            var slot = document.Type switch
            {
                IdentityDocumentTypeInfo.DrivingLicense => FunnelDocument.License,
                IdentityDocumentTypeInfo.InternationalDrivingPermit => FunnelDocument.Permit,
                _ => FunnelDocument.Identity,
            };
            var front = Staged(slot, FunnelSide.Front);
            var back = Staged(slot, FunnelSide.Back);
            if (front is not null || back is not null)
            {
                images.Add(new SubmittedDocumentImages(document.Type, front, back));
            }
        }

        return images;
    }

    private async Task<LockedQuoteInfo?> ReusableQuoteAsync(FunnelStartRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.QuoteCode))
        {
            return null;
        }

        var quote = await requests.GetLockedQuoteAsync(request.QuoteCode, cancellationToken);
        return quote is not null && Matches(quote, request, now) ? quote : null;
    }

    private async Task<FunnelSession?> FindAsync(string? token, CancellationToken cancellationToken, bool tracked = true)
    {
        if (!AccessSecrets.IsWellFormedToken(token))
        {
            return null;
        }

        var hash = AccessSecrets.HashToken(token!);
        var query = tracked ? db.Set<FunnelSession>() : db.Set<FunnelSession>().AsNoTracking();
        return await query.FirstOrDefaultAsync(s => s.TokenHash == hash, cancellationToken);
    }

    private async Task<Result<FunnelSession>> OpenForChangeAsync(string? token, CancellationToken cancellationToken)
    {
        var session = await FindAsync(token, cancellationToken);
        if (session is null)
        {
            return FunnelErrors.SessionNotFound;
        }

        if (session.IsSubmitted)
        {
            return FunnelErrors.AlreadySubmitted;
        }

        return session.IsExpiredAt(clock.GetUtcNow()) ? FunnelErrors.SessionExpired : session;
    }

    private async Task<Result> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return BookingErrors.ConcurrentUpdate;
        }
    }

    private async Task<IReadOnlyList<ExtraOffer>> OfferedExtrasAsync(FunnelSession session, CancellationToken cancellationToken) =>
        [.. (await pricing.GetExtrasAsync(session.AgencyId, session.Channel, cancellationToken)).Where(e => e.IsSelectable)];

    /// <summary>Prices the rental again with the plan in force and locks the new quote; the notice tells what changed.</summary>
    private async Task<Result> RelockAsync(
        FunnelSession session,
        IReadOnlyList<ExtraChoice> extras,
        FunnelDriversInput? drivers,
        CancellationToken cancellationToken)
    {
        var previous = await db.Set<Quote>().AsNoTracking().FirstOrDefaultAsync(q => q.Id == session.QuoteId, cancellationToken);
        var created = await requests.CreateLockedQuoteAsync(
            new PriceRequest(
                session.AgencyId,
                session.CategoryId,
                session.PickupLocationId,
                session.ReturnLocationId,
                session.PickupAt,
                session.ReturnAt,
                session.Channel,
                [.. extras.Select(e => new SelectedExtra(e.Code, e.Quantity))],
                BirthDates(drivers),
                previous?.PromoCode),
            cancellationToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var current = await db.Set<Quote>().AsNoTracking().FirstAsync(q => q.Id == created.Value.QuoteId, cancellationToken);
        string? notice = null;
        if (previous is not null && previous.RentalSubtotal != current.RentalSubtotal)
        {
            notice = "price_updated";
        }
        else if (current.Lines.Any(l => l.Kind == QuoteLineKind.YoungDriver) && previous?.Lines.Any(l => l.Kind == QuoteLineKind.YoungDriver) != true)
        {
            notice = "young_driver";
        }

        var relocked = session.Relock(created.Value.QuoteId, created.Value.Code, created.Value.ExpiresAtUtc, extras, notice, clock.GetUtcNow());
        return relocked.IsFailure ? relocked : await SaveAsync(cancellationToken);
    }

    private async Task<List<FunnelFieldError>> CheckDriversAsync(FunnelSession session, FunnelDriversInput input, CancellationToken cancellationToken)
    {
        var errors = new List<FunnelFieldError>();
        var main = FunnelDriverRules.Parse(input.Main, FunnelFields.Main, contactRequired: true, errors);
        var second = input.HasSecond ? FunnelDriverRules.Parse(input.Second ?? new FunnelDriverInput(), FunnelFields.Second, contactRequired: false, errors) : null;
        if (!input.DocumentProcessingConsent)
        {
            errors.Add(new FunnelFieldError(FunnelFields.DocumentConsent, FunnelErrors.DocumentConsentRequired.Code));
        }

        foreach (var (role, details) in new[] { (FunnelFields.Main, main), (FunnelFields.Second, second) })
        {
            if (details is null)
            {
                continue;
            }

            var check = await customers.CheckSubmittedDriverAsync(details, session.PickupAt, session.ReturnAt, session.AgencyId, cancellationToken);
            errors.AddRange(check.Errors.Select(e => new FunnelFieldError(FunnelDriverRules.FieldFor(role, e), e.Code)));
            if (check.Eligibility is { IsEligible: false } eligibility)
            {
                errors.AddRange(eligibility.Issues.Select(issue => FunnelDriverRules.ErrorFor(role, issue)));
            }
        }

        if (main is not null && second is not null
            && string.Equals(Key(main.Documents[1].Number), Key(second.Documents[1].Number), StringComparison.Ordinal)
            && string.Equals(main.Documents[1].IssuingCountry, second.Documents[1].IssuingCountry, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new FunnelFieldError($"{FunnelFields.Second}.{FunnelDriverRules.LicenseNumber}", BookingErrors.DriversIdentical.Code));
        }

        return errors;

        static string Key(string number) => new([.. number.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]);
    }

    private async Task<IReadOnlyList<FunnelUpload>> LatestUploadsAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await db.Set<FunnelUpload>()
            .AsNoTracking()
            .Where(u => u.SessionId == sessionId)
            .OrderBy(u => u.UploadedAtUtc)
            .ThenBy(u => u.Id)
            .ToListAsync(cancellationToken);

    private async Task<FunnelGuarantee> GuaranteeAsync(Guid agencyId, Money total, CancellationToken cancellationToken)
    {
        var mode = await settings.GetAsync(BookingSettings.ValidationPaymentMode, agencyId, cancellationToken);
        var ratio = await settings.GetAsync(PublicBookingSettings.GuaranteeRatio, agencyId, cancellationToken);
        return mode == BookingValidationPaymentMode.CaptureAtValidation && payments.IsAvailable
            ? new FunnelGuarantee(FunnelGuaranteeMode.PreAuthorization, GuaranteeAmount(total, ratio))
            : new FunnelGuarantee(FunnelGuaranteeMode.PaymentLinkAfterValidation, total);
    }

    private async Task<FunnelView?> ViewAsync(FunnelSession session, string? notice, bool firstVisit, CancellationToken cancellationToken)
    {
        var quote = await db.Set<Quote>().AsNoTracking().FirstOrDefaultAsync(q => q.Id == session.QuoteId, cancellationToken);
        if (quote is null)
        {
            return null;
        }

        var chosen = session.Extras.ToDictionary(e => e.Code, e => e.Quantity, StringComparer.Ordinal);
        var extras = (await OfferedExtrasAsync(session, cancellationToken))
            .Select(e => new FunnelExtraView(e.Code, e.Name, e.NameEn, e.Kind, e.Mode, e.Price, e.CapPerUnit, chosen.GetValueOrDefault(e.Code), MaxQuantity(e)))
            .ToList();
        var uploads = (await LatestUploadsAsync(session.Id, cancellationToken))
            .GroupBy(u => u.Slot)
            .Select(g => g.Last())
            .Select(u => new FunnelUploadView(u.Slot, u.Id, u.ContentType, u.UploadedAtUtc))
            .ToList();
        var view = quote.ToView();
        var contribution = view.Lines.Where(l => l.Kind == QuoteLineKind.DailyContribution).Select(l => l.InclVat).Sum();
        var inputs = QuoteSnapshots.ReadInputs(quote.InputsJson);
        return new FunnelView(
            session.Id,
            session.AgencyId,
            session.Culture,
            session.Channel,
            session.CategoryId,
            session.CategoryCode,
            session.PickupLocationId,
            session.ReturnLocationId,
            session.PickupAt,
            session.ReturnAt,
            session.SearchQuery,
            session.QuoteCode,
            session.ExpiresAtUtc,
            view,
            contribution,
            inputs?.CategoryDeposit ?? quote.Deposit,
            inputs?.CategoryReducedDeposit ?? quote.Deposit,
            extras,
            ReadDrivers(session.DriversJson),
            ReadDraft(session.DraftJson),
            uploads,
            session.DocumentProcessingConsent,
            session.WhatsAppNotificationsConsent,
            session.MarketingConsent,
            await GuaranteeAsync(session.AgencyId, quote.TotalInclVat, cancellationToken),
            notice,
            firstVisit,
            AttributionInfo(session.Attribution));
    }

    /// <summary>The confirmation of a request was shown: measured once per funnel session (funnel event).</summary>
    public async Task<FunnelConfirmationVisit?> MarkConfirmationShownAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var session = await db.Set<FunnelSession>().FirstOrDefaultAsync(s => s.BookingId == bookingId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var first = session.MarkTracked(FunnelSteps.Confirmation);
        if (first && (await SaveAsync(cancellationToken)).IsFailure)
        {
            first = false;
        }

        return new FunnelConfirmationVisit(first, session.CategoryCode, AttributionInfo(session.Attribution));
    }

    private static RequestAttributionInfo? AttributionInfo(BookingAttribution attribution) =>
        attribution.IsEmpty
            ? null
            : new RequestAttributionInfo(attribution.Source, attribution.UtmSource, attribution.UtmMedium, attribution.UtmCampaign, attribution.UtmTerm, attribution.UtmContent);

    private async Task<Result<FunnelSubmitted>> AbandonAsync(Guid bookingId, Error error, CancellationToken cancellationToken)
    {
        // What was registered for the request goes: pending submissions and their images, the profile.
        await registration.DiscardDriverSubmissionsAsync(bookingId, cancellationToken);
        var profile = await db.Set<BookingCustomerProfile>().FirstOrDefaultAsync(p => p.BookingId == bookingId, cancellationToken);
        if (profile is not null)
        {
            db.Set<BookingCustomerProfile>().Remove(profile);
            await db.SaveChangesAsync(cancellationToken);
        }

        LogSubmissionFailed(error.Code);
        return error;
    }

    /// <summary>Records the request on the session (a concurrent display of the session is retried once).</summary>
    private async Task MarkSubmittedAsync(Guid sessionId, Guid bookingId, string number, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var session = await db.Set<FunnelSession>().FirstAsync(s => s.Id == sessionId, cancellationToken);
            if (session.MarkSubmitted(bookingId, number, clock.GetUtcNow()).IsFailure)
            {
                return;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.Set<FunnelSession>().Entry(session).State = EntityState.Detached;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Funnel session {SessionId} opened for {Category} (quote reused: {Reused}).")]
    private partial void LogStarted(Guid sessionId, string category, bool reused);

    [LoggerMessage(Level = LogLevel.Information, Message = "Request {Number} submitted from funnel session {SessionId}.")]
    private partial void LogSubmitted(string number, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A funnel submission failed ({ErrorCode}): what was registered for it was discarded.")]
    private partial void LogSubmissionFailed(string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The guarantee of request {Number} could not be started ({ErrorCode}).")]
    private partial void LogGuaranteeFailed(string number, string errorCode);
}
