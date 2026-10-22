using CarRental.Domain.Common;

namespace CarRental.Application.Channels.Contracts;

/// <summary>
/// Read API of the Channels module for the pre-accounting and the reconciliations (package 3B): the monthly settlement of
/// each channel of an agency as the reconciliation screen of the channels computes it (ADR-0052) — the bookings of the
/// month, what the channel should pay and the payout entered by hand — so that the payouts and the commissions they
/// deduct reach the bank journal without a second screen. No permission check (the caller checks its own).
/// </summary>
public interface IChannelPayouts
{
    /// <summary>The settlement of each channel of the agency for the month of pick-up <paramref name="year"/>-<paramref name="month"/> (Tunis), by channel name.</summary>
    Task<IReadOnlyList<ChannelSettlementInfo>> GetSettlementsAsync(Guid agencyId, int year, int month, CancellationToken cancellationToken = default);

    /// <summary>
    /// The settlements whose payout was entered (or last corrected) on the Tunis days <paramref name="from"/> to
    /// <paramref name="to"/> (both included): the payouts to post in the bank journal of those days.
    /// </summary>
    Task<IReadOnlyList<ChannelSettlementInfo>> GetRecordedPayoutsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>
/// The settlement of a channel for a month of pick-up: bookings counted (not cancelled) and cancelled, their gross price,
/// commission and net; the gross and the net of the bookings prepaid to the channel, the commissions of the bookings paid
/// on site; what the channel should pay (<see cref="ExpectedSettlement"/> = prepaid net − commissions of the bookings paid
/// on site, TODO(spec): the contract of the channel) and the payout entered with its reference and when.
/// </summary>
public sealed record ChannelSettlementInfo(
    Guid ChannelId,
    string ChannelCode,
    string ChannelName,
    SalesChannel SalesChannel,
    int Year,
    int Month,
    int Bookings,
    int Cancelled,
    Money Gross,
    Money Commission,
    Money Net,
    Money PrepaidGross,
    Money PrepaidNet,
    Money PayOnSiteCommission,
    Money ExpectedSettlement,
    Guid? PayoutId,
    Money? Payout,
    string? PayoutReference,
    DateTimeOffset? PayoutRecordedAtUtc)
{
    /// <summary>The commissions the channel deducts from its payout: those of its prepaid bookings and those of the bookings paid on site.</summary>
    public Money DeductedCommission => PrepaidGross - ExpectedSettlement;

    /// <summary>Payout minus what the channel should pay; none until a payout is entered.</summary>
    public Money? Difference => Payout is { } payout ? payout - ExpectedSettlement : null;
}
