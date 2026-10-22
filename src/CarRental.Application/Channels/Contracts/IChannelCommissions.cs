using CarRental.Domain.Common;

namespace CarRental.Application.Channels.Contracts;

/// <summary>
/// The commissions of the channel bookings, for the indicators of the Reporting module ("CA et marge par canal après
/// commissions", SPEC §8). The Reporting port <c>IChannelCommissionSource</c> (lot 3C) adapts to this contract: one
/// line per booking with its pick-up and return (to spread it over the rented days like the revenue), or the
/// commission of given bookings.
/// </summary>
/// <remarks>
/// The commission of a booking is the amount reported by its channel, or the rate of the channel applied to the gross
/// price; a cancelled booking owes none unless the channel reported one (TODO(spec): the channel contract).
/// No permission check (module contract): the caller enforces its own.
/// </remarks>
public interface IChannelCommissions
{
    /// <summary>
    /// The channel bookings whose pick-up falls between <paramref name="from"/> and <paramref name="to"/> (Tunis dates,
    /// included), optionally for one agency, by pick-up.
    /// </summary>
    Task<IReadOnlyList<ChannelCommissionInfo>> GetCommissionsAsync(
        DateOnly from,
        DateOnly to,
        Guid? agencyId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The commission of each of <paramref name="bookingIds"/> created by a channel of <paramref name="agencyId"/>
    /// (absent: not a channel booking of the agency).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Money>> GetCommissionsByBookingAsync(
        Guid agencyId,
        IReadOnlyCollection<Guid> bookingIds,
        CancellationToken cancellationToken = default);
}

/// <summary>The commission of one channel booking and what it is computed on.</summary>
public sealed record ChannelCommissionInfo(
    Guid BookingId,
    string BookingNumber,
    Guid AgencyId,
    Guid ChannelId,
    string ChannelCode,
    SalesChannel SalesChannel,
    string ChannelReference,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    ChannelPaymentModeInfo PaymentMode,
    ChannelBookingStateInfo State,
    Money Gross,
    Money Commission,
    Money Net);
