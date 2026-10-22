using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Channels.Adapters;
using CarRental.Application.Channels.Restrictions;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Channels.Core;

/// <summary>How a booking enters through the core: from the channel (its restrictions apply) or by hand (already sold).</summary>
public sealed record ChannelEntryOptions(ChannelBookingSource Source, bool IgnoreChannelRestrictions, string? Notes = null)
{
    public static ChannelEntryOptions Inbound { get; } = new(ChannelBookingSource.Inbound, IgnoreChannelRestrictions: false);
}

/// <summary>
/// The canonical operations of the Channels module (docs/conception/phase-5-canaux.md §3), the same for every channel and
/// adapter: availability from the cached engine and priced by the pricing engine for the channel; creation of a
/// confirmed booking under the availability lock (idempotent by channel reference: a replay answers the same booking, a
/// reference reused with other details is refused), with the gross price, commission and net; modification and
/// cancellation (reason "channel"); reading. Every refusal carries a stable code; nothing here throws for a business
/// failure, and the channel always gets an answer.
/// </summary>
public sealed class ChannelReservationCore
{
    private readonly IAppDbContext _db;
    private readonly IChannelBookings _bookings;
    private readonly IBookingsQueries _bookingQueries;
    private readonly IAvailabilityQueries _availability;
    private readonly IPricingService _pricing;
    private readonly ChannelRestrictionReader _restrictions;
    private readonly TimeProvider _clock;

    internal ChannelReservationCore(
        IAppDbContext db,
        IChannelBookings bookings,
        IBookingsQueries bookingQueries,
        IAvailabilityQueries availability,
        IPricingService pricing,
        ChannelRestrictionReader restrictions,
        TimeProvider clock)
    {
        _db = db;
        _bookings = bookings;
        _bookingQueries = bookingQueries;
        _availability = availability;
        _pricing = pricing;
        _restrictions = restrictions;
        _clock = clock;
    }

    /// <summary>The categories the channel can sell for the request, with their remaining count and total price.</summary>
    public async Task<ChannelInboundOutcome> AvailabilityAsync(ChannelContext channel, ChannelAvailabilityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(request);
        if (Period.Create(request.PickupAt, request.ReturnAt) is not { IsSuccess: true } rental)
        {
            return ChannelInboundOutcome.Refusal(ChannelsErrors.PeriodInvalid);
        }

        if (channel.Mappings.LocationOf(request.PickupLocationCode) is not { } pickupId)
        {
            return ChannelInboundOutcome.Refusal(ChannelsErrors.LocationUnmapped);
        }

        var returnId = string.IsNullOrWhiteSpace(request.ReturnLocationCode) ? pickupId : channel.Mappings.LocationOf(request.ReturnLocationCode);
        if (returnId is null)
        {
            return ChannelInboundOutcome.Refusal(ChannelsErrors.LocationUnmapped);
        }

        Guid? requested = null;
        if (!string.IsNullOrWhiteSpace(request.CategoryCode))
        {
            requested = channel.Mappings.CategoryOf(request.CategoryCode);
            if (requested is null)
            {
                return ChannelInboundOutcome.Refusal(ChannelsErrors.CategoryUnmapped);
            }
        }

        var available = await _availability.GetAvailableCategoriesAsync(channel.AgencyId, rental.Value, channel.SalesChannel, cancellationToken);
        var offers = new List<ChannelOffer>();
        foreach (var candidate in available)
        {
            var categoryId = candidate.Category.Id;
            if ((requested is { } only && only != categoryId) || channel.Mappings.CodeOfCategory(categoryId) is not { } code)
            {
                continue;
            }

            var rules = await _restrictions.GetAsync(channel.AgencyId, categoryId, channel.SalesChannel, cancellationToken);
            if (ChannelRestrictionRules.StopsSellingAt(rules, pickupId, rental.Value))
            {
                continue;
            }

            var quote = await _pricing.QuoteAsync(
                new PriceRequest(channel.AgencyId, categoryId, pickupId, returnId.Value, request.PickupAt, request.ReturnAt, channel.SalesChannel),
                cancellationToken);
            if (quote.IsFailure)
            {
                // Not priced for these dates (minimum duration, no plan): the channel cannot sell it.
                continue;
            }

            offers.Add(new ChannelOffer(code, channel.Mappings.AcrissOf(categoryId), candidate.Remaining, quote.Value.Days, quote.Value.TotalInclVat.Amount, quote.Value.Deposit.Amount, Currency.Tnd.Code));
        }

        return ChannelInboundOutcome.Answer(new ChannelAvailabilityAnswer(offers));
    }

    /// <summary>Creates the confirmed booking of a reservation described with the channel's codes.</summary>
    public async Task<ChannelInboundOutcome> CreateAsync(
        ChannelContext channel,
        ChannelReservation reservation,
        IChannelAdapter adapter,
        ChannelEntryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(adapter);
        var mapped = adapter.MapBooking(reservation, channel.Mappings);
        return mapped.IsFailure
            ? ChannelInboundOutcome.Refusal(mapped.Error)
            : await CreateMappedAsync(channel, reservation, mapped.Value, options, cancellationToken);
    }

    /// <summary>
    /// Creates the confirmed booking of a reservation whose targets are known (the manual entry picks them directly; the
    /// channel codes of <paramref name="reservation"/> are then informative only).
    /// </summary>
    public async Task<ChannelInboundOutcome> CreateMappedAsync(
        ChannelContext channel,
        ChannelReservation reservation,
        MappedReservation mapped,
        ChannelEntryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(mapped);
        ArgumentNullException.ThrowIfNull(options);
        if (channel.State == ChannelState.Inactive)
        {
            return ChannelInboundOutcome.Refusal(ChannelsErrors.ChannelInactive);
        }

        var checkedTerms = Check(channel, reservation);
        if (checkedTerms.IsFailure)
        {
            return ChannelInboundOutcome.Refusal(checkedTerms.Error);
        }

        var (reference, amounts, paymentMode) = checkedTerms.Value;
        var fingerprint = Fingerprint(mapped, reservation, amounts, paymentMode);
        var existing = await FindAsync(channel.Id, reference, tracked: false, cancellationToken);
        if (existing is not null)
        {
            return string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal)
                ? await AnswerAsync(channel, existing, replayed: true, cancellationToken)
                : ChannelInboundOutcome.Refusal(ChannelsErrors.ReferenceConflict, existing.BookingId);
        }

        if (!options.IgnoreChannelRestrictions && await StopsSellingAsync(channel, mapped.CategoryId, mapped.PickupLocationId, reservation, cancellationToken))
        {
            return ChannelInboundOutcome.Refusal(ChannelsErrors.StopSell);
        }

        var created = await _bookings.CreateConfirmedAsync(
            new ChannelBookingRequest(
                channel.AgencyId,
                channel.SalesChannel,
                reference,
                mapped.CategoryId,
                mapped.PickupLocationId,
                mapped.ReturnLocationId,
                reservation.PickupAt,
                reservation.ReturnAt,
                new ChannelDriverInfo(reservation.Driver.FirstName.Trim(), reservation.Driver.LastName.Trim(), reservation.Driver.Phone, reservation.Driver.Email),
                mapped.Extras,
                options.Notes,
                options.IgnoreChannelRestrictions),
            async (booking, ct) =>
            {
                if (booking.AgencyId != channel.AgencyId)
                {
                    // References are unique per sales channel: one of another agency is never taken over.
                    return ChannelsErrors.ReferenceConflict;
                }

                var record = await FindAsync(channel.Id, reference, tracked: false, ct);
                if (record is not null)
                {
                    // Recorded meanwhile by a twin request of the same reference: a replay, or a conflicting reuse.
                    return string.Equals(record.RequestFingerprint, fingerprint, StringComparison.Ordinal)
                        ? Result.Success()
                        : ChannelsErrors.ReferenceConflict;
                }

                var recorded = ChannelBooking.Record(
                    new ChannelBookingDetails(
                        channel.Id,
                        channel.AgencyId,
                        channel.SalesChannel,
                        booking.BookingId,
                        booking.Number,
                        reference,
                        options.Source,
                        paymentMode,
                        booking.CategoryId,
                        booking.PickupAt,
                        booking.ReturnAt,
                        booking.TotalInclVat,
                        fingerprint),
                    amounts,
                    _clock.GetUtcNow());
                if (recorded.IsFailure)
                {
                    return recorded.Error;
                }

                _db.Set<ChannelBooking>().Add(recorded.Value);
                return Result.Success();
            },
            cancellationToken);
        if (created.IsFailure)
        {
            return ChannelInboundOutcome.Refusal(created.Error);
        }

        var saved = await FindAsync(channel.Id, reference, tracked: false, cancellationToken);
        return saved is null
            ? ChannelInboundOutcome.Refusal(ChannelsErrors.BookingNotFound, created.Value.BookingId)
            : await AnswerAsync(channel, saved, replayed: created.Value.Outcome == ChannelBookingOutcome.AlreadyExisted, cancellationToken);
    }

    /// <summary>Applies the channel's new terms to its booking (re-checked under the lock when the occupancy moves).</summary>
    public async Task<ChannelInboundOutcome> ModifyAsync(ChannelContext channel, ChannelReservation reservation, IChannelAdapter adapter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(adapter);
        var checkedTerms = Check(channel, reservation);
        if (checkedTerms.IsFailure)
        {
            return ChannelInboundOutcome.Refusal(checkedTerms.Error);
        }

        var (reference, amounts, paymentMode) = checkedTerms.Value;
        var existing = await FindAsync(channel.Id, reference, tracked: false, cancellationToken);
        if (existing is null)
        {
            return ChannelInboundOutcome.Refusal(ChannelsErrors.BookingNotFound);
        }

        var mapped = adapter.MapBooking(reservation, channel.Mappings);
        if (mapped.IsFailure)
        {
            return ChannelInboundOutcome.Refusal(mapped.Error, existing.BookingId);
        }

        if (await StopsSellingAsync(channel, mapped.Value.CategoryId, mapped.Value.PickupLocationId, reservation, cancellationToken))
        {
            return ChannelInboundOutcome.Refusal(ChannelsErrors.StopSell, existing.BookingId);
        }

        var amended = await _bookings.AmendAsync(
            new ChannelBookingAmendment(
                existing.BookingId,
                mapped.Value.CategoryId,
                mapped.Value.PickupLocationId,
                mapped.Value.ReturnLocationId,
                reservation.PickupAt,
                reservation.ReturnAt,
                mapped.Value.Extras),
            async (booking, ct) =>
            {
                var record = await _db.Set<ChannelBooking>().FirstOrDefaultAsync(b => b.Id == existing.Id, ct);
                if (record is null)
                {
                    return ChannelsErrors.BookingNotFound;
                }

                var unchanged = record.Gross == amounts.Gross
                    && record.Commission == amounts.Commission
                    && record.PaymentMode == paymentMode
                    && record.CategoryId == booking.CategoryId
                    && record.PickupAt == booking.PickupAt
                    && record.ReturnAt == booking.ReturnAt
                    && record.EngineTotalInclVat == booking.TotalInclVat;
                return unchanged
                    ? Result.Success()
                    : record.Amend(amounts, booking.CategoryId, booking.PickupAt, booking.ReturnAt, booking.TotalInclVat, paymentMode, _clock.GetUtcNow());
            },
            cancellationToken);
        if (amended.IsFailure)
        {
            return ChannelInboundOutcome.Refusal(amended.Error, existing.BookingId);
        }

        var saved = await FindAsync(channel.Id, reference, tracked: false, cancellationToken);
        return await AnswerAsync(channel, saved ?? existing, replayed: false, cancellationToken);
    }

    /// <summary>Cancels the channel's booking (idempotent); the channel may report the commission it keeps.</summary>
    public async Task<ChannelInboundOutcome> CancelAsync(ChannelContext channel, ChannelCancelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(request);
        var reference = ChannelCodes.Reference(request.Reference);
        if (reference.IsFailure)
        {
            return ChannelInboundOutcome.Refusal(reference.Error);
        }

        Money? reported = null;
        if (request.Commission is { } commission)
        {
            var amount = AmountOf(commission);
            if (amount.IsFailure)
            {
                return ChannelInboundOutcome.Refusal(amount.Error);
            }

            reported = amount.Value;
        }

        var existing = await FindAsync(channel.Id, reference.Value, tracked: false, cancellationToken);
        if (existing is null)
        {
            return ChannelInboundOutcome.Refusal(ChannelsErrors.BookingNotFound);
        }

        var cancelled = await _bookings.CancelAsync(
            existing.BookingId,
            async (_, ct) =>
            {
                var record = await _db.Set<ChannelBooking>().FirstOrDefaultAsync(b => b.Id == existing.Id, ct);
                return record is null ? ChannelsErrors.BookingNotFound : record.Cancel(reported, _clock.GetUtcNow());
            },
            cancellationToken);
        if (cancelled.IsFailure)
        {
            return ChannelInboundOutcome.Refusal(cancelled.Error, existing.BookingId);
        }

        var saved = await FindAsync(channel.Id, reference.Value, tracked: false, cancellationToken);
        return await AnswerAsync(channel, saved ?? existing, replayed: false, cancellationToken);
    }

    /// <summary>The state of the channel's booking.</summary>
    public async Task<ChannelInboundOutcome> ReadAsync(ChannelContext channel, string channelReference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var reference = ChannelCodes.Reference(channelReference);
        if (reference.IsFailure)
        {
            return ChannelInboundOutcome.Refusal(reference.Error);
        }

        var existing = await FindAsync(channel.Id, reference.Value, tracked: false, cancellationToken);
        return existing is null
            ? ChannelInboundOutcome.Refusal(ChannelsErrors.BookingNotFound)
            : await AnswerAsync(channel, existing, replayed: false, cancellationToken);
    }

    /// <summary>
    /// The fingerprint of a creation (SHA-256 of the internal targets, instants, amounts, payment mode and extras; no
    /// personal data): a replay has the same, a reuse of the reference with other details another.
    /// </summary>
    internal static string Fingerprint(MappedReservation mapped, ChannelReservation reservation, ChannelAmounts amounts, ChannelPaymentMode paymentMode)
    {
        var extras = string.Join(',', mapped.Extras.OrderBy(e => e.Code, StringComparer.Ordinal).Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Code}:{e.Quantity}")));
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"v1|{mapped.CategoryId:N}|{mapped.PickupLocationId:N}|{mapped.ReturnLocationId:N}|{reservation.PickupAt.UtcTicks}|{reservation.ReturnAt.UtcTicks}|{amounts.Gross.Amount:F3}|{amounts.Commission.Amount:F3}|{(int)paymentMode}|{extras}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static Result<(string Reference, ChannelAmounts Amounts, ChannelPaymentMode PaymentMode)> Check(ChannelContext channel, ChannelReservation reservation)
    {
        var reference = ChannelCodes.Reference(reservation.ChannelReference);
        if (reference.IsFailure)
        {
            return reference.Error;
        }

        if (reservation.ReturnAt <= reservation.PickupAt)
        {
            return ChannelsErrors.PeriodInvalid;
        }

        if (reservation.Driver is null || string.IsNullOrWhiteSpace(reservation.Driver.FirstName) || string.IsNullOrWhiteSpace(reservation.Driver.LastName))
        {
            return ChannelsErrors.DriverInvalid;
        }

        var gross = AmountOf(reservation.Gross);
        if (gross.IsFailure)
        {
            return gross.Error;
        }

        Money? commission = null;
        if (reservation.Commission is { } reportedCommission)
        {
            var amount = AmountOf(reportedCommission);
            if (amount.IsFailure)
            {
                return amount.Error;
            }

            commission = amount.Value;
        }

        Money? net = null;
        if (reservation.Net is { } reportedNet)
        {
            var amount = AmountOf(reportedNet);
            if (amount.IsFailure)
            {
                return amount.Error;
            }

            net = amount.Value;
        }

        var amounts = ChannelCommissionRule.Compute(gross.Value, commission, net, channel.CommissionRate);
        if (amounts.IsFailure)
        {
            return amounts.Error;
        }

        var paymentMode = reservation.PaymentMode switch
        {
            ChannelReservationPaymentMode.PrepaidToChannel => ChannelPaymentMode.PrepaidToChannel,
            ChannelReservationPaymentMode.PayOnSite => ChannelPaymentMode.PayOnSite,
            _ => channel.DefaultPaymentMode,
        };
        return (reference.Value, amounts.Value, paymentMode);
    }

    /// <summary>An amount of the channel in TND, millimes at most (TODO(spec): conversion of other currencies).</summary>
    private static Result<Money> AmountOf(ChannelAmount? amount)
    {
        if (amount is null)
        {
            return ChannelsErrors.AmountInvalid;
        }

        if (!string.Equals(amount.Currency?.Trim(), Currency.Tnd.Code, StringComparison.OrdinalIgnoreCase))
        {
            return ChannelsErrors.CurrencyNotSupported;
        }

        var money = Money.FromExact(amount.Amount, Currency.Tnd);
        return money.IsFailure ? ChannelsErrors.AmountInvalid : money.Value;
    }

    private async Task<bool> StopsSellingAsync(ChannelContext channel, Guid categoryId, Guid pickupLocationId, ChannelReservation reservation, CancellationToken cancellationToken)
    {
        var rules = await _restrictions.GetAsync(channel.AgencyId, categoryId, channel.SalesChannel, cancellationToken);
        return rules.Count > 0
            && Period.Create(reservation.PickupAt, reservation.ReturnAt) is { IsSuccess: true } rental
            && ChannelRestrictionRules.StopsSellingAt(rules, pickupLocationId, rental.Value);
    }

    private Task<ChannelBooking?> FindAsync(Guid channelId, string reference, bool tracked, CancellationToken cancellationToken)
    {
        var bookings = tracked ? _db.Set<ChannelBooking>() : _db.Set<ChannelBooking>().AsNoTracking();
        return bookings.FirstOrDefaultAsync(b => b.ChannelId == channelId && b.ChannelReference == reference, cancellationToken);
    }

    private async Task<ChannelInboundOutcome> AnswerAsync(ChannelContext channel, ChannelBooking record, bool replayed, CancellationToken cancellationToken)
    {
        var summary = await _bookingQueries.GetSummaryAsync(record.BookingId, cancellationToken);
        var view = new ChannelReservationView(
            record.ChannelReference,
            record.BookingNumber,
            StatusOf(summary?.Status, record.State),
            channel.Mappings.CodeOfCategory(summary?.CategoryId ?? record.CategoryId),
            summary is null ? null : channel.Mappings.CodeOfLocation(summary.PickupLocationId),
            summary is null ? null : channel.Mappings.CodeOfLocation(summary.ReturnLocationId),
            summary?.PickupAt ?? record.PickupAt,
            summary?.ReturnAt ?? record.ReturnAt,
            record.Gross.Amount,
            record.Commission.Amount,
            record.Net.Amount,
            Currency.Tnd.Code,
            record.PaymentMode == ChannelPaymentMode.PrepaidToChannel ? ChannelReservationPaymentMode.PrepaidToChannel : ChannelReservationPaymentMode.PayOnSite,
            record.EngineTotalInclVat.Amount);
        return ChannelInboundOutcome.Answer(new ChannelReservationAnswer(view, replayed), record.BookingId);
    }

    private static ChannelReservationStatus StatusOf(BookingStatusInfo? status, ChannelBookingState state) => status switch
    {
        BookingStatusInfo.InProgress => ChannelReservationStatus.InProgress,
        BookingStatusInfo.Returned or BookingStatusInfo.Closed => ChannelReservationStatus.Completed,
        BookingStatusInfo.Cancelled or BookingStatusInfo.Refused or BookingStatusInfo.Expired => ChannelReservationStatus.Cancelled,
        BookingStatusInfo.NoShow => ChannelReservationStatus.NoShow,
        null when state == ChannelBookingState.Cancelled => ChannelReservationStatus.Cancelled,
        _ => ChannelReservationStatus.Confirmed,
    };
}

/// <summary>The core bound to one channel and its adapter, as the adapter sees it (<see cref="IChannelInboundCore"/>).</summary>
internal sealed class BoundChannelCore(ChannelReservationCore core, ChannelContext channel, IChannelAdapter adapter) : IChannelInboundCore
{
    public Task<ChannelInboundOutcome> HandleAsync(ChannelInboundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request switch
        {
            ChannelAvailabilityRequest availability => core.AvailabilityAsync(channel, availability, cancellationToken),
            ChannelCreateRequest create => core.CreateAsync(channel, create.Reservation, adapter, ChannelEntryOptions.Inbound, cancellationToken),
            ChannelModifyRequest modify => core.ModifyAsync(channel, modify.Reservation, adapter, cancellationToken),
            ChannelCancelRequest cancel => core.CancelAsync(channel, cancel, cancellationToken),
            ChannelReadRequest read => core.ReadAsync(channel, read.Reference, cancellationToken),
            _ => Task.FromResult(ChannelInboundOutcome.Refusal(ChannelsErrors.InboundUnsupported)),
        };
    }
}
