using CarRental.Domain.Common;

namespace CarRental.Domain.Channels;

// Channels
public sealed record ChannelCreated(Guid ChannelId, Guid AgencyId, string Code, SalesChannel SalesChannel) : IDomainEvent;

public sealed record ChannelSettingsChanged(Guid ChannelId) : IDomainEvent;

public sealed record ChannelStateChanged(Guid ChannelId, ChannelState PreviousState, ChannelState State) : IDomainEvent;

/// <summary>What changed in the inbound authentication of a channel (never the secret itself).</summary>
public enum ChannelCredentialsChange
{
    ApiKeyRotated = 1,
    ApiKeyRevoked = 2,
    Certificate = 3,
    IpRanges = 4,
}

public sealed record ChannelCredentialsChanged(Guid ChannelId, ChannelCredentialsChange Change) : IDomainEvent;

public sealed record ChannelMappingsChanged(Guid ChannelId, ChannelMappingKind Kind) : IDomainEvent;

// Quotas and stop-sells
public sealed record ChannelQuotaAdded(Guid QuotaId, Guid ChannelId, Guid CategoryId, ChannelQuotaKind Kind) : IDomainEvent;

public sealed record ChannelQuotaChanged(Guid QuotaId, Guid ChannelId, Guid CategoryId, ChannelQuotaKind Kind) : IDomainEvent;

public sealed record ChannelQuotaRemoved(Guid QuotaId, Guid ChannelId, Guid CategoryId, ChannelQuotaKind Kind) : IDomainEvent;

// Channel bookings
public sealed record ChannelBookingRecorded(Guid ChannelBookingId, Guid ChannelId, Guid BookingId, ChannelBookingSource Source) : IDomainEvent;

public sealed record ChannelBookingAmended(Guid ChannelBookingId, Guid ChannelId, Guid BookingId) : IDomainEvent;

public sealed record ChannelBookingCancelled(Guid ChannelBookingId, Guid ChannelId, Guid BookingId) : IDomainEvent;

// Reconciliation
public sealed record ChannelPayoutRecorded(Guid PayoutId, Guid ChannelId, int Year, int Month) : IDomainEvent;
