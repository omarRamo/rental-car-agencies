using CarRental.Application.Channels.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Channels.Queries;

/// <summary>
/// The Reporting port of the channel commissions (ADR-0046) served by this module: the commission of each channel
/// booking of the agency as <see cref="IChannelCommissions"/> records it (reported by the channel, or its rate applied to
/// the gross price).
/// </summary>
/// <remarks>TODO(spec): the VAT on the commission depends on the channel contract; the recorded amount is used as is.</remarks>
internal sealed class ChannelCommissionReportingSource(IChannelCommissions commissions) : IChannelCommissionSource
{
    public Task<IReadOnlyDictionary<Guid, Money>> GetCommissionsAsync(
        Guid agencyId,
        IReadOnlyCollection<Guid> bookingIds,
        CancellationToken cancellationToken = default) =>
        commissions.GetCommissionsByBookingAsync(agencyId, bookingIds, cancellationToken);
}
