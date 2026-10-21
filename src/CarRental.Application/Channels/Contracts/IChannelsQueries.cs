using CarRental.Domain.Common;

namespace CarRental.Application.Channels.Contracts;

/// <summary>
/// Public read API of the Channels module for other modules (ids and contract DTOs only): the channels of an agency and
/// the channel side of a rental file created by a channel (gross price, commission, net, payment mode).
/// </summary>
/// <remarks>No permission check here (like the other <c>I{Module}Queries</c>): the caller enforces its own.</remarks>
public interface IChannelsQueries
{
    /// <summary>The channels of an agency (of every agency when <see langword="null"/>), by name.</summary>
    Task<IReadOnlyList<ChannelSummaryInfo>> GetChannelsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default);

    /// <summary>The channel side of a rental file, or <see langword="null"/> when no channel created it.</summary>
    Task<ChannelBookingInfo?> GetChannelBookingAsync(Guid bookingId, CancellationToken cancellationToken = default);
}

/// <summary>What the agency does with a channel (same values as the domain enum).</summary>
public enum ChannelStateInfo
{
    Inactive = 1,
    Manual = 2,
    Connected = 3,
}

/// <summary>The adapter of a channel (same values as the domain enum).</summary>
public enum ChannelAdapterKindInfo
{
    Manual = 1,
    Fake = 2,
    BookingCom = 3,
}

/// <summary>How the customer of a channel booking pays (same values as the domain enum).</summary>
public enum ChannelPaymentModeInfo
{
    PrepaidToChannel = 1,
    PayOnSite = 2,
}

/// <summary>Where a channel booking came from (same values as the domain enum).</summary>
public enum ChannelBookingSourceInfo
{
    Inbound = 1,
    Manual = 2,
}

/// <summary>State of a channel booking, mirrored from the rental file (same values as the domain enum).</summary>
public enum ChannelBookingStateInfo
{
    Active = 1,
    Cancelled = 2,
    NoShow = 3,
}

/// <summary>Where the commission comes from (same values as the domain enum).</summary>
public enum ChannelCommissionSourceInfo
{
    Rule = 1,
    Reported = 2,
}

/// <summary>A channel of an agency.</summary>
public sealed record ChannelSummaryInfo(
    Guid Id,
    Guid AgencyId,
    string Code,
    string Name,
    SalesChannel SalesChannel,
    ChannelStateInfo State,
    ChannelAdapterKindInfo Adapter,
    ChannelPaymentModeInfo DefaultPaymentMode,
    decimal CommissionRate);

/// <summary>
/// The channel side of a rental file: its reference in the channel, where it came from, its state, payment mode, gross
/// price, commission (rule or reported) and net, and the total of the engine when it was received.
/// </summary>
public sealed record ChannelBookingInfo(
    Guid BookingId,
    string BookingNumber,
    Guid ChannelId,
    string ChannelCode,
    string ChannelName,
    SalesChannel SalesChannel,
    string ChannelReference,
    ChannelBookingSourceInfo Source,
    ChannelBookingStateInfo State,
    ChannelPaymentModeInfo PaymentMode,
    Money Gross,
    Money Commission,
    Money Net,
    ChannelCommissionSourceInfo CommissionSource,
    Money EngineTotalInclVat,
    DateTimeOffset ReceivedAtUtc);
