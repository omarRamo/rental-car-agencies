using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Parties;
using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Bookings.Writing;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings;

/// <summary>
/// Quick creation of a booking by an agent (SPEC §6 "création rapide", phone and counter): a dated option or a booking
/// confirmed at once (<c>CreateOption</c>, <c>CreateConfirmed</c>), priced by the engine and written under the
/// availability lock with its readable number (ADR-0032), for the agency of the agent only (ADR-0067).
/// </summary>
public sealed class QuickBookingService
{
    private static readonly SalesChannel[] AgentChannels = [SalesChannel.Phone, SalesChannel.Counter, SalesChannel.WhatsApp, SalesChannel.BookingCom];

    private readonly BookingWriter _writer;
    private readonly BookingPricing _pricing;
    private readonly QuoteStore _quotes;
    private readonly DriverChecks _checks;
    private readonly ICustomerRegistration _registration;
    private readonly ISettingsReader _settings;

    internal QuickBookingService(
        BookingWriter writer,
        BookingPricing pricing,
        QuoteStore quotes,
        DriverChecks checks,
        ICustomerRegistration registration,
        ISettingsReader settings)
    {
        _writer = writer;
        _pricing = pricing;
        _quotes = quotes;
        _checks = checks;
        _registration = registration;
        _settings = settings;
    }

    public async Task<Result<QuickBookingResult>> CreateAsync(QuickBookingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_writer.User.HasPermission(Permissions.BookingsManage) || !_writer.WorksFor(request.AgencyId))
        {
            return BookingErrors.Forbidden;
        }

        if (!AgentChannels.Contains(request.Channel) || !Enum.IsDefined(request.Outcome))
        {
            return BookingErrors.ChannelInvalid;
        }

        var extras = BookingPricing.ChoicesOf(request.Extras);
        if (extras.IsFailure)
        {
            return extras.Error;
        }

        var attribution = BookingAttribution.Create(request.Source);
        if (attribution.IsFailure)
        {
            return attribution.Error;
        }

        var drivers = new BookingParties(request.MainDriverId, request.SecondaryDriverId, request.PayerCustomerId);
        if (await _checks.AnyFailsCustomerCheckAsync(drivers.Drivers, cancellationToken))
        {
            return BookingErrors.CustomerCheckFailed;
        }

        var terms = new RentalTerms(
            request.AgencyId,
            request.Channel,
            request.CategoryId,
            request.PickupLocationId,
            request.ReturnLocationId,
            request.PickupAt,
            request.ReturnAt,
            extras.Value,
            request.PromoCode,
            request.GuaranteedVehicleId,
            drivers);
        var priced = await _pricing.QuoteAsync(terms, cancellationToken);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        if (request.ExpectedTotalInclVat is { } expected && expected != priced.Value.TotalInclVat)
        {
            return BookingErrors.QuotePriceChanged;
        }

        var content = QuoteSnapshots.ToContent(priced.Value);
        var optionDuration = await _settings.GetAsync(BookingSettings.OptionDefaultDuration, request.AgencyId, cancellationToken);
        return await _writer.RunAsync<QuickBookingResult>(
            async (touched, ct) =>
            {
                var now = _writer.Now();
                DateTimeOffset? optionExpiresAt = null;
                if (request.Outcome == QuickBookingOutcome.Option)
                {
                    var expiry = OptionExpiry(request, optionDuration, now);
                    if (expiry.IsFailure)
                    {
                        return expiry.Error;
                    }

                    optionExpiresAt = expiry.Value;
                }

                var payer = request.PayerCustomerId is { } payerId
                    ? Result.Success(payerId)
                    : await _registration.GetOrCreateIndividualCustomerAsync(request.MainDriverId, ct);
                if (payer.IsFailure)
                {
                    return payer.Error;
                }

                // A reference of a channel is unique within the agency (each agency has its own channel accounts).
                var reference = request.ChannelReference?.Trim();
                if (!string.IsNullOrEmpty(reference)
                    && await _writer.Db.Set<Booking>().AnyAsync(b => b.AgencyId == request.AgencyId && b.Channel == request.Channel && b.ChannelReference == reference, ct))
                {
                    return BookingErrors.ChannelReferenceTaken;
                }

                var bookingId = Entity.NewId();
                var rental = Period.Create(request.PickupAt, request.ReturnAt);
                if (rental.IsFailure)
                {
                    return BookingErrors.PeriodInvalid;
                }

                var available = await _writer.Guard.EnsureAvailableAsync(
                    new AvailabilityRequest(
                        request.CategoryId,
                        request.Channel,
                        new BookingOccupancy(bookingId, OccupancyKind.Planned, rental.Value, assignedVehicleId: request.GuaranteedVehicleId))
                    {
                        // A Booking.com booking is already sold by the channel: only the physical capacity counts.
                        IgnoreChannelRestrictions = request.Channel == SalesChannel.BookingCom,
                    },
                    cancellationToken: ct);
                if (available.IsFailure)
                {
                    return available.Error;
                }

                var number = await _writer.NextNumberAsync(request.AgencyId, now, ct);
                var actor = _writer.Actor(request.Channel);
                var quote = await _quotes.AddVersionAsync(bookingId, 1, QuoteOrigin.Initial, content, actor.Name, now, ct);
                if (quote.IsFailure)
                {
                    return quote.Error;
                }

                var details = new BookingDetails(
                    request.AgencyId,
                    request.Channel,
                    request.CategoryId,
                    request.PickupLocationId,
                    request.ReturnLocationId,
                    request.PickupAt,
                    request.ReturnAt,
                    drivers with { PayerCustomerId = payer.Value },
                    extras.Value,
                    request.PromoCode,
                    request.GuaranteedVehicleId,
                    reference,
                    attribution.Value,
                    request.Notes);
                var created = optionExpiresAt is { } optionExpiry
                    ? Booking.CreateOption(bookingId, details, number, quote.Value.ToReference(), optionExpiry, actor, now)
                    : Booking.CreateConfirmed(bookingId, details, number, quote.Value.ToReference(), actor, now);
                if (created.IsFailure)
                {
                    return created.Error;
                }

                _writer.Db.Set<Booking>().Add(created.Value);
                _writer.PublishStatusChange(created.Value, null, now);
                touched.Add(request.CategoryId);
                return new QuickBookingResult(created.Value.Id, created.Value.Number, created.Value.Status, created.Value.TotalInclVat, created.Value.OptionExpiresAt);
            },
            cancellationToken);
    }

    /// <summary>The chosen expiry (in the future, not after the pick-up), or the default duration capped at the pick-up.</summary>
    private static Result<DateTimeOffset> OptionExpiry(QuickBookingRequest request, TimeSpan defaultDuration, DateTimeOffset now)
    {
        if (request.OptionExpiresAt is { } chosen)
        {
            return chosen <= now || chosen > request.PickupAt ? BookingErrors.OptionExpiryInvalid : chosen.ToUniversalTime();
        }

        var expiry = now + defaultDuration;
        if (expiry > request.PickupAt)
        {
            expiry = request.PickupAt;
        }

        return expiry <= now ? BookingErrors.OptionExpiryInvalid : expiry;
    }
}
