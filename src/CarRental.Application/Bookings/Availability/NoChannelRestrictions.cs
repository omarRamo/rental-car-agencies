using CarRental.Application.Bookings.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Availability;

/// <summary>Phase 1 channel policy: no quota and no stop-sell (the Channels module replaces it in phase 5).</summary>
internal sealed class NoChannelRestrictions : IChannelAvailabilityPolicy
{
    public Task<ChannelRestrictionsInfo> GetRestrictionsAsync(
        Guid categoryId,
        SalesChannel channel,
        Period range,
        CancellationToken cancellationToken = default) => Task.FromResult(ChannelRestrictionsInfo.None);
}
