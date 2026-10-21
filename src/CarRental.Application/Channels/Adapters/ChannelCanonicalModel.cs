using CarRental.Domain.Common;

namespace CarRental.Application.Channels.Adapters;

// The canonical format of the Channels module (docs/conception/phase-5-canaux.md §2): requests and answers in C#
// objects. Each adapter translates the format of its channel into these types and back; the core never sees a wire
// format, and no channel format is invented here.

/// <summary>The operations a channel can ask in real time.</summary>
public enum ChannelInboundOperation
{
    Availability = 1,
    Create = 2,
    Modify = 3,
    Cancel = 4,
    Read = 5,
}

/// <summary>A request of a channel, in canonical form.</summary>
public abstract record ChannelInboundRequest(ChannelInboundOperation Operation)
{
    /// <summary>The reference of the booking in the channel, for the operations on one booking.</summary>
    public abstract string? ChannelReference { get; }
}

/// <summary>Which categories can be rented from a location and for how much (channel codes, instants with their offset).</summary>
public sealed record ChannelAvailabilityRequest(
    string PickupLocationCode,
    string? ReturnLocationCode,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    string? CategoryCode = null) : ChannelInboundRequest(ChannelInboundOperation.Availability)
{
    public override string? ChannelReference => null;
}

/// <summary>A booking sold by the channel, to create confirmed.</summary>
public sealed record ChannelCreateRequest(ChannelReservation Reservation) : ChannelInboundRequest(ChannelInboundOperation.Create)
{
    public override string? ChannelReference => Reservation.ChannelReference;
}

/// <summary>The new terms of a booking of the channel.</summary>
public sealed record ChannelModifyRequest(ChannelReservation Reservation) : ChannelInboundRequest(ChannelInboundOperation.Modify)
{
    public override string? ChannelReference => Reservation.ChannelReference;
}

/// <summary>A booking of the channel cancelled; the channel may report the commission it keeps.</summary>
public sealed record ChannelCancelRequest(string Reference, ChannelAmount? Commission = null) : ChannelInboundRequest(ChannelInboundOperation.Cancel)
{
    public override string? ChannelReference => Reference;
}

/// <summary>The state of a booking of the channel.</summary>
public sealed record ChannelReadRequest(string Reference) : ChannelInboundRequest(ChannelInboundOperation.Read)
{
    public override string? ChannelReference => Reference;
}

/// <summary>
/// A booking as a channel describes it, with the channel's codes: category, places, instants, main driver, gross price,
/// commission and net (when the channel reports them), payment mode and extras (OpenTravel codes mapped by the agency).
/// <see cref="Remarks"/> (flight number…) is kept out of the message log.
/// </summary>
public sealed record ChannelReservation(
    string ChannelReference,
    string CategoryCode,
    string PickupLocationCode,
    string ReturnLocationCode,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    ChannelDriver Driver,
    ChannelAmount Gross,
    ChannelAmount? Commission = null,
    ChannelAmount? Net = null,
    ChannelReservationPaymentMode? PaymentMode = null,
    IReadOnlyList<ChannelExtraRequest>? Extras = null,
    string? Remarks = null);

/// <summary>The main driver of a channel booking (personal data: never logged).</summary>
public sealed record ChannelDriver(string FirstName, string LastName, string? Phone = null, string? Email = null);

/// <summary>An amount in the currency the channel reports it in (only TND is accepted: TODO(spec) for conversions).</summary>
public sealed record ChannelAmount(decimal Amount, string Currency)
{
    public static ChannelAmount Tnd(decimal amount) => new(amount, global::CarRental.Domain.Common.Currency.Tnd.Code);

    public static ChannelAmount Of(Money money) => new(money.Amount, money.Currency.Code);
}

/// <summary>How the customer pays a channel booking, as the canonical format carries it.</summary>
public enum ChannelReservationPaymentMode
{
    PrepaidToChannel = 1,
    PayOnSite = 2,
}

/// <summary>Which list of the channel an option code belongs to.</summary>
public enum ChannelOptionKind
{
    /// <summary>Equipment and services (child seat, additional driver…).</summary>
    Extra = 1,

    /// <summary>Coverages (franchise waiver…).</summary>
    Insurance = 2,

    /// <summary>Fees (one way, after hours, delivery…).</summary>
    Fee = 3,
}

/// <summary>An option of a channel booking: its list, the channel's code and the quantity.</summary>
public sealed record ChannelExtraRequest(ChannelOptionKind Kind, string Code, int Quantity);

/// <summary>An answer of the core to a channel request, in canonical form.</summary>
public abstract record ChannelInboundResponse;

/// <summary>The categories that can be rented, priced by the engine for the channel.</summary>
public sealed record ChannelAvailabilityAnswer(IReadOnlyList<ChannelOffer> Offers) : ChannelInboundResponse;

/// <summary>A category that can be rented: channel and ACRISS codes, vehicles left, days charged, total and deposit (TND).</summary>
public sealed record ChannelOffer(string CategoryCode, string AcrissCode, int Remaining, int Days, decimal TotalInclVat, decimal Deposit, string Currency);

/// <summary>A booking of the channel after an operation; <see cref="Replayed"/>: a creation already done, answered again.</summary>
public sealed record ChannelReservationAnswer(ChannelReservationView Reservation, bool Replayed) : ChannelInboundResponse;

/// <summary>State of a channel booking as answered to the channel.</summary>
public enum ChannelReservationStatus
{
    Confirmed = 1,
    InProgress = 2,
    Completed = 3,
    Cancelled = 4,
    NoShow = 5,
}

/// <summary>A booking of the channel as the platform knows it (channel codes, amounts in TND).</summary>
public sealed record ChannelReservationView(
    string ChannelReference,
    string BookingNumber,
    ChannelReservationStatus Status,
    string? CategoryCode,
    string? PickupLocationCode,
    string? ReturnLocationCode,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    decimal Gross,
    decimal Commission,
    decimal Net,
    string Currency,
    ChannelReservationPaymentMode PaymentMode,
    decimal EngineTotalInclVat);

/// <summary>What the core did with a request: an answer, or a refusal with a stable error code (never an exception).</summary>
public sealed record ChannelInboundOutcome
{
    private ChannelInboundOutcome(ChannelInboundResponse? response, Error? error, Guid? bookingId)
    {
        Response = response;
        Error = error;
        BookingId = bookingId;
    }

    public ChannelInboundResponse? Response { get; }

    /// <summary>The refusal: <c>availability.not_available</c>, <c>channels.mapping.*</c>, <c>channels.booking.*</c>…</summary>
    public Error? Error { get; }

    /// <summary>The rental file concerned, when known (message log).</summary>
    public Guid? BookingId { get; }

    public bool IsSuccess => Error is null;

    public static ChannelInboundOutcome Answer(ChannelInboundResponse response, Guid? bookingId = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new ChannelInboundOutcome(response, null, bookingId);
    }

    public static ChannelInboundOutcome Refusal(Error error, Guid? bookingId = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ChannelInboundOutcome(null, error, bookingId);
    }
}

/// <summary>Availability of the mapped categories to publish: per category, location and run of days with the same count.</summary>
public sealed record ChannelAvailabilityUpdate(IReadOnlyList<ChannelAvailabilityItem> Items);

/// <summary>Vehicles that can still be sold each day from <see cref="From"/> to <see cref="To"/> (included) at a location.</summary>
public sealed record ChannelAvailabilityItem(string CategoryCode, string AcrissCode, string LocationCode, DateOnly From, DateOnly To, int Available);

/// <summary>Daily prices of the engine for the channel, per category and run of days with the same price.</summary>
public sealed record ChannelRatesUpdate(string Currency, IReadOnlyList<ChannelRateItem> Items);

/// <summary>The daily price TTC (after the channel rule) of a category each day from <see cref="From"/> to <see cref="To"/> (included).</summary>
public sealed record ChannelRateItem(string CategoryCode, string AcrissCode, DateOnly From, DateOnly To, decimal DailyPriceInclVat);

/// <summary>The channel an outbound update goes to.</summary>
public sealed record ChannelPushTarget(Guid ChannelId, string ChannelCode, SalesChannel SalesChannel);
