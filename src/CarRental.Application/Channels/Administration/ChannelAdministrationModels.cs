using CarRental.Domain.Channels;
using CarRental.Domain.Common;

namespace CarRental.Application.Channels.Administration;

/// <summary>A channel in the list of the back office, with what needs attention.</summary>
public sealed record ChannelListItem(
    Guid Id,
    Guid AgencyId,
    string AgencyName,
    string Code,
    string Name,
    SalesChannel SalesChannel,
    ChannelState State,
    ChannelAdapterKind AdapterKind,
    ChannelPaymentMode DefaultPaymentMode,
    decimal CommissionRate,
    bool HasApiKey,
    bool HasCertificate,
    int AllowedRangeCount,
    int MappedCategories,
    int MappedLocations,
    int PendingReminders,
    int FailedUpdates);

/// <summary>A channel to create (inactive at first).</summary>
public sealed record CreateChannelRequest(
    Guid AgencyId,
    string Code,
    string Name,
    SalesChannel SalesChannel,
    ChannelAdapterKind AdapterKind,
    ChannelPaymentMode DefaultPaymentMode,
    decimal CommissionRate);

/// <summary>The settings of a channel: name, state, adapter, default payment mode and commission rate.</summary>
public sealed record UpdateChannelSettingsRequest(
    string Name,
    ChannelState State,
    ChannelAdapterKind AdapterKind,
    ChannelPaymentMode DefaultPaymentMode,
    decimal CommissionRate);

/// <summary>A category of the agency and its mapping on the channel, if any.</summary>
public sealed record CategoryMappingView(Guid CategoryId, string AcrissCode, string Name, bool IsActive, Guid? MappingId, string? ChannelCode, string? SoldAsAcriss);

/// <summary>A location of the agency and its mapping on the channel, if any.</summary>
public sealed record LocationMappingView(Guid LocationId, string Code, string Name, Guid? MappingId, string? ChannelCode);

/// <summary>An extra, insurance or fee mapping: the rate plan code and the OpenTravel code entered by the agency.</summary>
public sealed record OptionMappingView(Guid MappingId, ChannelMappingKind Kind, string InternalCode, string? ExtraName, string ChannelCode);

/// <summary>An extra of the published plan that can be mapped.</summary>
public sealed record MappableExtraView(string Code, string Name);

/// <summary>Everything the channel screen shows and edits.</summary>
public sealed record ChannelDetailView(
    Guid Id,
    Guid AgencyId,
    string AgencyName,
    string Code,
    string Name,
    SalesChannel SalesChannel,
    ChannelState State,
    ChannelAdapterKind AdapterKind,
    IReadOnlyList<ChannelAdapterKind> AvailableAdapters,
    ChannelPaymentMode DefaultPaymentMode,
    decimal CommissionRate,
    string? ApiKeyHint,
    DateTimeOffset? ApiKeyCreatedAtUtc,
    string? ClientCertificateThumbprint,
    IReadOnlyList<string> AllowedIpRanges,
    IReadOnlyList<CategoryMappingView> Categories,
    IReadOnlyList<LocationMappingView> Locations,
    IReadOnlyList<OptionMappingView> Options,
    IReadOnlyList<MappableExtraView> Extras,
    DateTimeOffset UpdatedAtUtc,
    string? UpdatedBy);

/// <summary>A quota or stop-sell of a channel, with the names to show.</summary>
public sealed record RestrictionView(
    Guid Id,
    Guid ChannelId,
    ChannelQuotaKind Kind,
    Guid CategoryId,
    string CategoryCode,
    Guid? LocationId,
    string? LocationCode,
    DateOnly StartDate,
    DateOnly EndDate,
    int ReservedVehicles,
    string? Note,
    DateTimeOffset UpdatedAtUtc,
    string? UpdatedBy);

/// <summary>A quota or stop-sell to add or change.</summary>
public sealed record RestrictionRequest(
    ChannelQuotaKind Kind,
    Guid CategoryId,
    Guid? LocationId,
    DateOnly StartDate,
    DateOnly EndDate,
    int ReservedVehicles,
    string? Note);

/// <summary>A channel that can receive a manual entry, with its categories and locations.</summary>
public sealed record ManualEntryChannelView(
    Guid ChannelId,
    string Name,
    string Code,
    Guid AgencyId,
    ChannelState State,
    ChannelPaymentMode DefaultPaymentMode,
    decimal CommissionRate,
    IReadOnlyList<ManualEntryOptionView> Categories,
    IReadOnlyList<ManualEntryOptionView> Locations);

/// <summary>A choice of the manual entry form.</summary>
public sealed record ManualEntryOptionView(Guid Id, string Code, string Name);

/// <summary>
/// A booking of a channel entered by hand from its extranet: reference, places, instants, category, main driver, gross
/// price (VAT included), commission (empty: the rate of the channel) and payment mode (empty: the channel's default).
/// </summary>
public sealed record ManualEntryRequest(
    Guid ChannelId,
    string ChannelReference,
    Guid CategoryId,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    string DriverFirstName,
    string DriverLastName,
    string? DriverPhone,
    string? DriverEmail,
    decimal Gross,
    decimal? Commission,
    ChannelPaymentMode? PaymentMode,
    string? Notes);

/// <summary>The commission and net a manual entry would record, and the price of the engine for comparison.</summary>
public sealed record ManualEntryPreview(Money Gross, Money Commission, Money Net, ChannelCommissionSource CommissionSource, Money? EngineTotalInclVat, string? PricingError);

/// <summary>The booking recorded by a manual entry; <see cref="Replayed"/>: the same entry was already recorded.</summary>
public sealed record ManualEntryResult(Guid BookingId, string BookingNumber, bool Replayed, Money EngineTotalInclVat, Money Gross);

/// <summary>A change to replicate on the extranet of a manual channel.</summary>
public sealed record ReplicationView(
    Guid Id,
    Guid ChannelId,
    string ChannelName,
    ChannelReplicationKind Kind,
    string? CategoryCode,
    string? LocationCode,
    DateOnly? StartDate,
    DateOnly? EndDate,
    int? Vehicles,
    string? Detail,
    DateTimeOffset CreatedAtUtc,
    string? CreatedBy,
    DateTimeOffset? AcknowledgedAtUtc,
    string? AcknowledgedBy);

/// <summary>Filters of the message log.</summary>
public sealed record MessageLogFilter(
    Guid? ChannelId = null,
    ChannelMessageDirection? Direction = null,
    ChannelMessageOperation? Operation = null,
    ChannelMessageStatus? Status = null,
    string? Reference = null,
    DateOnly? From = null,
    DateOnly? To = null);

/// <summary>A row of the message log.</summary>
public sealed record MessageLogEntry(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    string ChannelCode,
    ChannelMessageDirection Direction,
    ChannelMessageOperation Operation,
    ChannelMessageStatus Status,
    string? ChannelReference,
    int? HttpStatus,
    int DurationMilliseconds,
    string? ErrorCode,
    string? Excerpt,
    string? RemoteAddress,
    Guid? BookingId);

/// <summary>Which updates of the queue to list.</summary>
public enum SyncQueueFilter
{
    All = 0,
    Pending = 1,
    Failed = 2,
    Sent = 3,
}

/// <summary>An update of the outbound queue.</summary>
public sealed record SyncItemEntry(
    Guid Id,
    Guid ChannelId,
    string ChannelCode,
    ChannelSyncOperation Operation,
    string? CategoryCode,
    string Reason,
    ChannelSyncStatus Status,
    int Attempts,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset NextAttemptAtUtc,
    DateTimeOffset? SentAtUtc,
    string? LastError,
    string? Payload);

/// <summary>State of the outbound queue (screen and health entry <c>channels</c>).</summary>
public sealed record ChannelSyncStatistics(int FailedCount, int PendingCount, int RetryingCount, DateTimeOffset? OldestDueAtUtc, int PendingReminders);

/// <summary>A channel booking of the month in the reconciliation.</summary>
public sealed record ReconciliationBookingView(
    Guid BookingId,
    string BookingNumber,
    string ChannelReference,
    DateTimeOffset PickupAt,
    ChannelBookingState State,
    ChannelPaymentMode PaymentMode,
    ChannelBookingSource Source,
    Money Gross,
    Money Commission,
    Money Net,
    Money EngineTotalInclVat)
{
    /// <summary>Gross price of the channel minus the price of the engine.</summary>
    public Money PriceDifference => Gross - EngineTotalInclVat;
}

/// <summary>The reconciliation of a channel for a month (bookings by Tunis pick-up date).</summary>
public sealed record ReconciliationView(
    Guid ChannelId,
    string ChannelName,
    int Year,
    int Month,
    ChannelMonthFigures Figures,
    IReadOnlyList<ReconciliationBookingView> Bookings,
    string? PayoutReference,
    string? PayoutNote,
    DateTimeOffset? PayoutRecordedAtUtc,
    string? PayoutRecordedBy);
