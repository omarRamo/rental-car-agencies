using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Bookings.Writing;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings;

/// <summary><see cref="IBookingRequests"/>: locked quotes and web requests of the public funnel (phase 2) and the bot.</summary>
internal sealed class BookingRequestService(
    BookingWriter writer,
    BookingPricing pricing,
    QuoteStore quotes,
    ICustomerRegistration registration,
    ISettingsReader settings) : IBookingRequests
{
    private static readonly SalesChannel[] RequestChannels = [SalesChannel.Web, SalesChannel.WhatsApp];

    public async Task<Result<LockedQuoteInfo>> CreateLockedQuoteAsync(PriceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var extras = BookingPricing.ChoicesOf(request.Extras);
        if (extras.IsFailure)
        {
            return extras.Error;
        }

        var priced = await pricing.QuoteAsync(TermsOf(request, extras.Value, BookingParties.None), cancellationToken);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        var content = QuoteSnapshots.ToContent(priced.Value);
        return await writer.RunAsync<LockedQuoteInfo>(
            async (_, ct) =>
            {
                var code = await quotes.NewCodeAsync(ct);
                if (code.IsFailure)
                {
                    return code.Error;
                }

                var now = writer.Now();
                var quote = Quote.CreateLocked(content, code.Value, writer.Actor().Name, now);
                if (quote.IsFailure)
                {
                    return quote.Error;
                }

                writer.Db.Set<Quote>().Add(quote.Value);
                return ToInfo(quote.Value, now);
            },
            cancellationToken);
    }

    public async Task<LockedQuoteInfo?> GetLockedQuoteAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = QuoteCode.Normalize(code);
        if (normalized.IsFailure)
        {
            return null;
        }

        var quote = await quotes.FindLockedAsync(normalized.Value, cancellationToken);
        return quote is null ? null : ToInfo(quote, writer.Now());
    }

    public async Task<Result<BookingRequestResult>> CreateDraftAsync(BookingRequestDraft draft, CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(draft, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error;
        }

        return await writer.RunAsync<BookingRequestResult>(
            async (touched, ct) =>
            {
                var created = await CreateDraftAsync(prepared.Value, ct);
                if (created.IsFailure)
                {
                    return created.Error;
                }

                touched.Add(created.Value.CategoryId);
                return ResultOf(created.Value);
            },
            cancellationToken);
    }

    public async Task<Result<BookingRequestResult>> SubmitAsync(Guid bookingId, BookingRequestParties parties, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parties);
        return await writer.RunAsync<BookingRequestResult>(
            async (touched, ct) =>
            {
                var booking = await writer.LoadAsync(bookingId, ct);
                if (booking is null)
                {
                    return BookingErrors.NotFound;
                }

                var submitted = await SubmitAsync(booking, parties, ct);
                if (submitted.IsFailure)
                {
                    return submitted.Error;
                }

                touched.Add(booking.CategoryId);
                return ResultOf(booking);
            },
            cancellationToken);
    }

    public async Task<Result<BookingRequestResult>> SubmitRequestAsync(
        BookingRequestDraft draft,
        BookingRequestParties parties,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parties);
        var prepared = await PrepareAsync(draft, cancellationToken, parties);
        if (prepared.IsFailure)
        {
            return prepared.Error;
        }

        return await writer.RunAsync<BookingRequestResult>(
            async (touched, ct) =>
            {
                // The availability lock before the number, as every writer takes them (lock, then number): a request and
                // a phone booking racing for the same category would otherwise wait on each other (deadlock).
                var locked = await writer.Guard.LockAsync([prepared.Value.Content.CategoryId], ct);
                if (locked.IsFailure)
                {
                    return locked.Error;
                }

                var created = await CreateDraftAsync(prepared.Value, ct);
                if (created.IsFailure)
                {
                    return created.Error;
                }

                var submitted = await SubmitAsync(created.Value, parties, ct);
                if (submitted.IsFailure)
                {
                    return submitted.Error;
                }

                touched.Add(created.Value.CategoryId);
                return ResultOf(created.Value);
            },
            cancellationToken);
    }

    public Task<Result> ProvideInformationAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        writer.TransitionAsync(
            bookingId,
            async (booking, now, ct) =>
            {
                var available = await writer.Guard.EnsureAvailableAsync(
                    new AvailabilityRequest(booking.CategoryId, booking.Channel, booking.OccupancyAfter(booking.Rental, booking.AssignedVehicleId))
                    {
                        ComplianceWaived = booking.IsComplianceWaived,
                    },
                    cancellationToken: ct);
                return available.IsFailure ? available : booking.ProvideInformation(writer.Actor(booking.Channel), now);
            },
            cancellationToken);

    private static RentalTerms TermsOf(PriceRequest request, IReadOnlyList<ExtraChoice> extras, BookingParties parties) => new(
        request.AgencyId,
        request.Channel,
        request.CategoryId,
        request.PickupLocationId,
        request.ReturnLocationId,
        request.PickupAt,
        request.ReturnAt,
        extras,
        request.PromoCode,
        request.GuaranteedVehicleId,
        parties);

    private static LockedQuoteInfo ToInfo(Quote quote, DateTimeOffset now)
    {
        var inputs = QuoteSnapshots.ReadInputs(quote.InputsJson);
        return new LockedQuoteInfo(
            quote.Id,
            quote.Code,
            quote.AgencyId,
            quote.CategoryId,
            quote.Channel,
            quote.PickupLocationId,
            quote.ReturnLocationId,
            quote.PickupAt,
            quote.ReturnAt,
            quote.Days,
            quote.TotalInclVat,
            quote.Deposit,
            inputs?.Extras ?? [],
            quote.PromoCode,
            inputs?.GuaranteedVehicleId,
            quote.ExpiresAtUtc ?? quote.CreatedAtUtc,
            quote.IsExpiredAt(now));
    }

    private static BookingRequestResult ResultOf(Booking booking) =>
        new(booking.Id, booking.Number, booking.Status.ToInfo(), booking.TotalInclVat, booking.Deposit, booking.HoldExpiresAt);

    /// <summary>The checked request and its frozen price (from the locked quote, or priced now), outside the transaction.</summary>
    private async Task<Result<PreparedRequest>> PrepareAsync(
        BookingRequestDraft draft,
        CancellationToken cancellationToken,
        BookingRequestParties? parties = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!RequestChannels.Contains(draft.Channel) || (draft.LockedQuoteCode is null) == (draft.Price is null))
        {
            return BookingErrors.ChannelInvalid;
        }

        var attribution = draft.Attribution is { } source
            ? BookingAttribution.Create(source.Source, source.UtmSource, source.UtmMedium, source.UtmCampaign, source.UtmTerm, source.UtmContent)
            : Result.Success(BookingAttribution.None);
        if (attribution.IsFailure)
        {
            return attribution.Error;
        }

        var driverParties = parties is null ? BookingParties.None : new BookingParties(parties.MainDriverId, parties.SecondaryDriverId, parties.PayerCustomerId);
        if (draft.LockedQuoteCode is { } code)
        {
            var normalized = QuoteCode.Normalize(code);
            if (normalized.IsFailure)
            {
                return normalized.Error;
            }

            var locked = await quotes.FindLockedAsync(normalized.Value, cancellationToken);
            if (locked is null)
            {
                return BookingErrors.QuoteNotFound;
            }

            if (locked.IsExpiredAt(writer.Now()))
            {
                return BookingErrors.QuoteExpired;
            }

            var inputs = QuoteSnapshots.ReadInputs(locked.InputsJson);
            var lockedExtras = BookingPricing.ChoicesOf(inputs?.Extras);
            if (lockedExtras.IsFailure)
            {
                return lockedExtras.Error;
            }

            return new PreparedRequest(
                draft,
                locked.ToContent(),
                QuoteOrigin.LockedQuote,
                locked.Id,
                lockedExtras.Value,
                inputs?.GuaranteedVehicleId,
                attribution.Value);
        }

        var price = draft.Price!;
        var extras = BookingPricing.ChoicesOf(price.Extras);
        if (extras.IsFailure)
        {
            return extras.Error;
        }

        var priced = await pricing.QuoteAsync(TermsOf(price with { Channel = draft.Channel }, extras.Value, driverParties), cancellationToken);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        return new PreparedRequest(draft, QuoteSnapshots.ToContent(priced.Value), QuoteOrigin.Initial, null, extras.Value, price.GuaranteedVehicleId, attribution.Value);
    }

    /// <summary>Creates the draft and its first quote version in the current transaction (a draft occupies nothing: no lock).</summary>
    private async Task<Result<Booking>> CreateDraftAsync(PreparedRequest request, CancellationToken cancellationToken)
    {
        var now = writer.Now();
        var bookingId = request.Draft.BookingId is { } chosen && chosen != Guid.Empty ? chosen : Entity.NewId();
        var number = await writer.NextNumberAsync(request.Content.AgencyId, now, cancellationToken);
        var actor = writer.Actor(request.Draft.Channel);
        var quote = await quotes.AddVersionAsync(bookingId, 1, request.Origin, request.Content, actor.Name, now, cancellationToken, request.SourceQuoteId);
        if (quote.IsFailure)
        {
            return quote.Error;
        }

        var content = request.Content;
        var created = Booking.CreateDraft(
            bookingId,
            new BookingDetails(
                content.AgencyId,
                request.Draft.Channel,
                content.CategoryId,
                content.PickupLocationId,
                content.ReturnLocationId,
                content.PickupAt,
                content.ReturnAt,
                BookingParties.None,
                request.Extras,
                content.PromoCode,
                request.GuaranteedVehicleId,
                ChannelReference: null,
                request.Attribution,
                request.Draft.Notes),
            number,
            quote.Value.ToReference(),
            actor,
            now);
        if (created.IsFailure)
        {
            return created.Error;
        }

        writer.Db.Set<Booking>().Add(created.Value);
        writer.PublishStatusChange(created.Value, null, now);
        return created.Value;
    }

    /// <summary>Payer, consent and the guarded submission of a draft, in the current transaction.</summary>
    private async Task<Result> SubmitAsync(Booking booking, BookingRequestParties parties, CancellationToken cancellationToken)
    {
        var payer = parties.PayerCustomerId is { } payerId
            ? Result.Success(payerId)
            : await registration.GetOrCreateIndividualCustomerAsync(parties.MainDriverId, cancellationToken);
        if (payer.IsFailure)
        {
            return payer.Error;
        }

        if (parties.WhatsAppNotificationsConsent)
        {
            var consent = await registration.SetConsentAsync(
                parties.MainDriverId,
                ConsentPurposeInfo.WhatsAppNotifications,
                booking.Channel == SalesChannel.WhatsApp ? ConsentChannelInfo.WhatsApp : ConsentChannelInfo.Web,
                given: true,
                cancellationToken);
            if (consent.IsFailure)
            {
                return consent.Error;
            }
        }

        var available = await writer.Guard.EnsureAvailableAsync(
            new AvailabilityRequest(booking.CategoryId, booking.Channel, booking.OccupancyAfter(booking.Rental, booking.AssignedVehicleId)),
            cancellationToken: cancellationToken);
        if (available.IsFailure)
        {
            return available;
        }

        var hold = await settings.GetAsync(WellKnownSettings.BookingHoldDuration, booking.AgencyId, cancellationToken);
        var before = booking.Status;
        var now = writer.Now();
        var submitted = booking.Submit(
            new BookingParties(parties.MainDriverId, parties.SecondaryDriverId, payer.Value),
            hold,
            writer.Actor(booking.Channel),
            now);
        if (submitted.IsSuccess)
        {
            writer.PublishStatusChange(booking, before, now);
        }

        return submitted;
    }

    private sealed record PreparedRequest(
        BookingRequestDraft Draft,
        QuoteContent Content,
        QuoteOrigin Origin,
        Guid? SourceQuoteId,
        IReadOnlyList<ExtraChoice> Extras,
        Guid? GuaranteedVehicleId,
        BookingAttribution Attribution);
}
