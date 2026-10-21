namespace CarRental.Domain.Channels;

/// <summary>
/// Which adapter translates the format of a channel (docs/conception/phase-5-canaux.md §2). Values are stored: never
/// renumber them.
/// </summary>
public enum ChannelAdapterKind
{
    /// <summary>No network: bookings are entered in the back office, changes are replicated by hand on the extranet.</summary>
    Manual = 1,

    /// <summary>Development and test channel speaking a small JSON format documented as TEST-ONLY.</summary>
    Fake = 2,

    /// <summary>Booking.com XML/API connection. TODO(spec): written once the integration specification is delivered.</summary>
    BookingCom = 3,
}

/// <summary>What the agency does with a channel.</summary>
public enum ChannelState
{
    /// <summary>Not selling: inbound messages are refused, nothing is sent, no reminder is produced.</summary>
    Inactive = 1,

    /// <summary>
    /// Selling through the channel's extranet: bookings are entered by hand and every quota, stop-sell or price change
    /// joins the "to replicate on the extranet" list.
    /// </summary>
    Manual = 2,

    /// <summary>Connected through its adapter: inbound requests are answered and changes are pushed.</summary>
    Connected = 3,
}

/// <summary>How the customer of a channel booking pays (SPEC §4 "Règles par canal").</summary>
public enum ChannelPaymentMode
{
    /// <summary>Prepaid to the channel, which pays the agency the net amount.</summary>
    PrepaidToChannel = 1,

    /// <summary>Paid at the counter; the channel invoices its commission.</summary>
    PayOnSite = 2,
}

/// <summary>What a mapping translates between the platform and a channel.</summary>
public enum ChannelMappingKind
{
    /// <summary>A vehicle category (ACRISS code) and the code of the channel.</summary>
    Category = 1,

    /// <summary>A pick-up or return location (airports TUN, NBE, MIR, DJE…) and the code of the channel.</summary>
    Location = 2,

    /// <summary>An extra of the rate plan and its OpenTravel Alliance code, entered by the agency.</summary>
    Extra = 3,

    /// <summary>An insurance (franchise waiver…) and its OpenTravel Alliance code, entered by the agency.</summary>
    Insurance = 4,

    /// <summary>A fee (one way, after hours, delivery…) and its OpenTravel Alliance code, entered by the agency.</summary>
    Fee = 5,
}

/// <summary>Kind of commercial restriction of a channel on a category (SPEC §7 "Quotas et arrêt des ventes par canal").</summary>
public enum ChannelQuotaKind
{
    /// <summary>Vehicles of the category kept for the other channels (e.g. keep 2 out of Booking.com in high season).</summary>
    Reserve = 1,

    /// <summary>The channel cannot sell the category during the period (optionally at one pick-up location only).</summary>
    StopSell = 2,
}

/// <summary>Direction of a message exchanged with a channel.</summary>
public enum ChannelMessageDirection
{
    Inbound = 1,
    Outbound = 2,
}

/// <summary>What a message exchanged with a channel was about.</summary>
public enum ChannelMessageOperation
{
    /// <summary>The message could not be read far enough to know.</summary>
    Unknown = 0,

    Availability = 1,
    Create = 2,
    Modify = 3,
    Cancel = 4,
    Read = 5,

    /// <summary>A booking of the channel entered by hand in the back office.</summary>
    ManualEntry = 6,

    PushAvailability = 7,
    PushRates = 8,
}

/// <summary>Outcome of a message exchanged with a channel.</summary>
public enum ChannelMessageStatus
{
    /// <summary>Processed: answered, created, modified, cancelled or sent.</summary>
    Accepted = 1,

    /// <summary>Refused for a business reason (no vehicle left, stop-sell, unmapped code…), with a clear code.</summary>
    Refused = 2,

    /// <summary>Rejected by the security checks (unknown channel, address not allowed, missing or wrong credentials).</summary>
    Rejected = 3,

    /// <summary>Malformed message or technical failure.</summary>
    Failed = 4,
}

/// <summary>What an outbound synchronization item sends.</summary>
public enum ChannelSyncOperation
{
    /// <summary>Availability of one category (or of every mapped category).</summary>
    PushAvailability = 1,

    /// <summary>Daily prices of the pricing engine for the channel.</summary>
    PushRates = 2,

    /// <summary>Availability and prices of every mapped category (nightly, or after a mapping change).</summary>
    FullResync = 3,
}

/// <summary>State of an outbound synchronization item.</summary>
public enum ChannelSyncStatus
{
    /// <summary>Waiting to be sent, or waiting for its next attempt after a failure.</summary>
    Pending = 1,

    /// <summary>Accepted by the channel.</summary>
    Sent = 2,

    /// <summary>Failed for good: no automatic attempt left (health entry <c>channels</c> degraded, replay from the screen).</summary>
    Failed = 3,
}

/// <summary>Where a channel booking came from.</summary>
public enum ChannelBookingSource
{
    /// <summary>Received from the channel through its adapter.</summary>
    Inbound = 1,

    /// <summary>Entered by hand in the back office from the channel's extranet.</summary>
    Manual = 2,
}

/// <summary>State of a channel booking, mirrored from the rental file.</summary>
public enum ChannelBookingState
{
    /// <summary>Confirmed, in progress or returned: the commission is due.</summary>
    Active = 1,

    /// <summary>Cancelled: no commission, unless the channel reported one.</summary>
    Cancelled = 2,

    /// <summary>The customer did not show up. TODO(spec): the contract says whether a commission is due; it is kept until then.</summary>
    NoShow = 3,
}

/// <summary>Where the commission of a channel booking comes from.</summary>
public enum ChannelCommissionSource
{
    /// <summary>The percentage of the channel applied to the gross price.</summary>
    Rule = 1,

    /// <summary>The amount reported by the channel (it wins over the rule).</summary>
    Reported = 2,
}

/// <summary>A change to replicate by hand on the extranet of a channel sold manually.</summary>
public enum ChannelReplicationKind
{
    QuotaAdded = 1,
    QuotaChanged = 2,
    QuotaRemoved = 3,
    StopSellAdded = 4,
    StopSellChanged = 5,
    StopSellRemoved = 6,

    /// <summary>A new rate plan (or a new channel rule) was published: the prices of the extranet must follow.</summary>
    RatesPublished = 7,
}
