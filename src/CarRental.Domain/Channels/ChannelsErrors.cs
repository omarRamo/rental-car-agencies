using CarRental.Domain.Common;

namespace CarRental.Domain.Channels;

/// <summary>
/// Business errors of the Channels module (codes <c>channels.*</c>, stable: channels receive them as refusal codes;
/// texts in <c>SharedResources</c> as <c>Error.{code}</c>). Errors of the engines a channel booking relies on keep their
/// own codes (<c>availability.*</c>, <c>pricing.*</c>, <c>booking.*</c>).
/// </summary>
public static class ChannelsErrors
{
    // Cross-cutting
    public static readonly Error Forbidden = Error.Forbidden("channels.forbidden", "The current user lacks the permission to manage the channels.");

    public static readonly Error NotFound = Error.NotFound("channels.not_found", "The channel does not exist.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "channels.concurrent_update",
        "The channel data was changed by someone else in the meantime; reload and try again.");

    public static readonly Error AgencyRequired = Error.Validation("channels.agency_required", "An agency is required.");

    public static readonly Error AgencyNotFound = Error.NotFound("channels.agency_not_found", "The agency does not exist.");

    // Channel
    public static readonly Error CodeInvalid = Error.Validation(
        "channels.code_invalid",
        "A channel code has 2 to 32 characters: lower-case letters, digits and inner dashes (e.g. booking-com).");

    public static readonly Error CodeTaken = Error.Conflict("channels.code_taken", "Another channel already uses this code.");

    public static readonly Error NameInvalid = Error.Validation("channels.name_invalid", $"A channel name has 1 to {Channel.NameMaxLength} characters.");

    public static readonly Error SalesChannelInvalid = Error.Validation(
        "channels.sales_channel_invalid",
        "A channel carries the bookings of a comparator (Booking.com).");

    public static readonly Error SalesChannelTaken = Error.Conflict(
        "channels.sales_channel_taken",
        "The agency already has a channel for this sales channel.");

    public static readonly Error AdapterInvalid = Error.Validation("channels.adapter_invalid", "Unknown channel adapter.");

    public static readonly Error PaymentModeInvalid = Error.Validation("channels.payment_mode_invalid", "Unknown payment mode.");

    public static readonly Error CommissionRateInvalid = Error.Validation(
        "channels.commission_rate_invalid",
        "A commission rate is between 0 % and 100 %, with at most two decimals of percent.");

    public static readonly Error StateInvalid = Error.Validation("channels.state_invalid", "Unknown channel state.");

    public static readonly Error StateRequiresAdapter = Error.Conflict(
        "channels.state_requires_adapter",
        "A connected channel needs a network adapter: choose one before connecting it, or make the channel manual first.");

    /// <summary>The adapter of the channel cannot talk to it yet (Booking.com: TODO(spec), the integration specification).</summary>
    public static readonly Error AdapterNotConfigured = Error.Failure(
        "channels.adapter_not_configured",
        "The adapter of this channel is not configured: its message format is only delivered after the supplier application is accepted.");

    public static readonly Error AdapterUnavailable = Error.Validation(
        "channels.adapter_unavailable",
        "This adapter is not available in this environment (the test channel only exists in development and tests).");

    // Inbound security
    public static readonly Error IpRangeInvalid = Error.Validation(
        "channels.ip_range_invalid",
        "An allowed address is an IPv4 or IPv6 address, optionally with a prefix length (CIDR, e.g. 203.0.113.0/24).");

    public static readonly Error IpRangesTooMany = Error.Validation(
        "channels.ip_ranges_too_many",
        $"At most {IpAllowlist.MaxRanges} allowed address ranges.");

    public static readonly Error CertificateThumbprintInvalid = Error.Validation(
        "channels.certificate_thumbprint_invalid",
        "A certificate thumbprint is the SHA-1 (40) or SHA-256 (64) hexadecimal digits of the certificate.");

    // Mappings
    public static readonly Error MappingKindInvalid = Error.Validation("channels.mapping.kind_invalid", "Unknown kind of mapping.");

    public static readonly Error MappingTargetRequired = Error.Validation("channels.mapping.target_required", "Choose what the code corresponds to.");

    public static readonly Error MappingCodeInvalid = Error.Validation(
        "channels.mapping.code_invalid",
        $"A code of the channel has 1 to {ChannelCodes.MappingCodeMaxLength} characters: letters, digits, '.', '_', '-' or '/'.");

    public static readonly Error MappingInternalCodeInvalid = Error.Validation(
        "channels.mapping.internal_code_invalid",
        "An extra code has 2 to 32 characters: upper-case letters, digits and underscores, starting with a letter.");

    public static readonly Error MappingAcrissInvalid = Error.Validation("channels.mapping.acriss_invalid", "The ACRISS code is not valid.");

    public static readonly Error MappingCodeTaken = Error.Conflict(
        "channels.mapping.code_taken",
        "This code of the channel is already mapped to something else of the same kind.");

    public static readonly Error MappingNotFound = Error.NotFound("channels.mapping.not_found", "The mapping does not exist.");

    public static readonly Error MappingCategoryNotFound = Error.NotFound(
        "channels.mapping.category_not_found",
        "The vehicle category does not exist in the agency of the channel.");

    public static readonly Error MappingLocationNotFound = Error.NotFound(
        "channels.mapping.location_not_found",
        "The location does not exist in the agency of the channel.");

    /// <summary>A channel message names a category the agency has not mapped (refusal, never a guess).</summary>
    public static readonly Error CategoryUnmapped = Error.Validation("channels.mapping.category_unmapped", "The category code of the channel is not mapped.");

    public static readonly Error LocationUnmapped = Error.Validation("channels.mapping.location_unmapped", "The location code of the channel is not mapped.");

    public static readonly Error ExtraUnmapped = Error.Validation("channels.mapping.extra_unmapped", "An extra, insurance or fee code of the channel is not mapped.");

    // Quotas and stop-sells
    public static readonly Error RestrictionNotFound = Error.NotFound("channels.restriction.not_found", "The quota or stop-sell does not exist.");

    public static readonly Error RestrictionCategoryRequired = Error.Validation("channels.restriction.category_required", "A vehicle category is required.");

    public static readonly Error RestrictionCategoryNotFound = Error.NotFound(
        "channels.restriction.category_not_found",
        "The vehicle category does not exist in the agency of the channel.");

    public static readonly Error RestrictionLocationNotFound = Error.NotFound(
        "channels.restriction.location_not_found",
        "The location does not exist in the agency of the channel.");

    public static readonly Error RestrictionPeriodInvalid = Error.Validation(
        "channels.restriction.period_invalid",
        $"The last day comes after the first one, {ChannelQuota.MaxDays} days at most.");

    public static readonly Error RestrictionEnded = Error.Validation("channels.restriction.ended", "The restriction must not end before today.");

    public static readonly Error RestrictionVehiclesInvalid = Error.Validation(
        "channels.restriction.vehicles_invalid",
        $"Keep 1 to {ChannelQuota.MaxReservedVehicles} vehicles for the other channels.");

    public static readonly Error RestrictionLocationNotAllowed = Error.Validation(
        "channels.restriction.location_not_allowed",
        "Only a stop-sell can be limited to one pick-up location; vehicles are kept for the whole category.");

    public static readonly Error RestrictionNoteTooLong = Error.Validation(
        "channels.restriction.note_too_long",
        $"A note has {ChannelQuota.NoteMaxLength} characters at most.");

    // Channel bookings
    public static readonly Error ReferenceInvalid = Error.Validation(
        "channels.booking.reference_invalid",
        $"A channel reference has 1 to {ChannelCodes.ReferenceMaxLength} characters: letters, digits, '.', '_', '-' or '/'.");

    /// <summary>The reference already created a booking with other details: a change is a modification, not a new creation.</summary>
    public static readonly Error ReferenceConflict = Error.Conflict(
        "channels.booking.reference_conflict",
        "This channel reference already created a booking with other details; send a modification instead.");

    public static readonly Error BookingNotFound = Error.NotFound("channels.booking.not_found", "No booking of this channel carries this reference.");

    public static readonly Error BookingCancelled = Error.Conflict("channels.booking.cancelled", "The booking is cancelled.");

    public static readonly Error AmountInvalid = Error.Validation(
        "channels.booking.amount_invalid",
        "Gross price, commission and net are amounts in Tunisian dinars; the commission is between zero and the gross price.");

    public static readonly Error AmountsInconsistent = Error.Validation(
        "channels.booking.amounts_inconsistent",
        "The net price must be the gross price minus the commission.");

    public static readonly Error CurrencyNotSupported = Error.Validation(
        "channels.booking.currency_not_supported",
        "Amounts must be in Tunisian dinars (TND). TODO(spec): conversion rules of the channel contract.");

    public static readonly Error DriverInvalid = Error.Validation(
        "channels.booking.driver_invalid",
        "The main driver needs a first name and a last name.");

    public static readonly Error PeriodInvalid = Error.Validation("channels.booking.period_invalid", "The return must come after the pick-up.");

    /// <summary>A stop-sell of the pick-up location applies to the channel.</summary>
    public static readonly Error StopSell = Error.Conflict("channels.booking.stop_sell", "Sales are stopped on this channel at this pick-up location.");

    public static readonly Error ChannelInactive = Error.Conflict("channels.booking.channel_inactive", "The channel is inactive: it does not sell.");

    // Inbound messages
    public static readonly Error InboundUnknownChannel = Error.NotFound("channels.inbound.unknown_channel", "No channel has this code.");

    public static readonly Error InboundNotConnected = Error.Forbidden("channels.inbound.not_connected", "The channel is not connected.");

    public static readonly Error InboundAddressNotAllowed = Error.Forbidden(
        "channels.inbound.address_not_allowed",
        "The calling address is not in the allowed addresses of the channel.");

    public static readonly Error InboundUnauthorized = Error.Forbidden(
        "channels.inbound.unauthorized",
        "The message does not carry the credentials of the channel (API key or client certificate).");

    public static readonly Error InboundMalformed = Error.Validation("channels.inbound.malformed", "The message cannot be read in the format of the channel.");

    public static readonly Error InboundUnsupported = Error.Validation(
        "channels.inbound.unsupported_operation",
        "The channel does not offer this operation at this address.");

    public static readonly Error InboundPayloadTooLarge = Error.Validation("channels.inbound.payload_too_large", "The message is too large.");

    public static readonly Error InboundFailed = Error.Failure("channels.inbound.failed", "The message could not be processed; send it again.");

    // Outbound synchronization
    public static readonly Error SyncItemNotFound = Error.NotFound("channels.sync.not_found", "The synchronization item does not exist.");

    public static readonly Error PushFailed = Error.Failure("channels.sync.push_failed", "The channel did not accept the update.");

    // Extranet reminders and reconciliation
    public static readonly Error ReplicationNotFound = Error.NotFound("channels.replication.not_found", "The change to replicate does not exist.");

    public static readonly Error PayoutMonthInvalid = Error.Validation("channels.payout.month_invalid", "Choose a month between 2000 and 2100.");

    public static readonly Error PayoutAmountInvalid = Error.Validation(
        "channels.payout.amount_invalid",
        "A payout is zero or a positive amount in Tunisian dinars, with millimes at most.");

    public static readonly Error PayoutTextTooLong = Error.Validation(
        "channels.payout.text_too_long",
        $"A reference has {ChannelPayout.ReferenceMaxLength} characters at most and a note {ChannelPayout.NoteMaxLength}.");
}
