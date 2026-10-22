using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Contracts;

/// <summary>
/// Write API of the Channels module (docs/conception/phase-5-canaux.md §3): a booking of a channel such as Booking.com
/// arrives <b>confirmed</b> (distributor rule), under the availability lock like every other channel, with its channel
/// reference (unique per sales channel: the idempotency key); the channel can then modify or cancel it ("motif :
/// canal"). Prices always come from the pricing engine for the channel (parity, markup or own grid); the amounts
/// reported by the channel (gross, commission, net) are kept by the Channels module. The reference is unique within the
/// agency of the booking (each agency has its own channel accounts, ADR-0067): an operation of one agency writes for
/// that agency only.
/// </summary>
/// <remarks>
/// <para>Requires <c>bookings.manage</c> and <c>customers.manage</c> (the driver is found again or registered): the
/// inbound endpoint runs as a system actor, the manual entry as the manager.</para>
/// <para><c>alongside</c> runs in the same unit of work, right after the booking was written (or found): the Channels
/// module records its own data there, and a failure undoes everything. It may run again after a transient failure,
/// like the whole operation: load what it needs inside.</para>
/// <para>Idempotent: creating a reference that already exists returns that booking
/// (<see cref="ChannelBookingOutcome.AlreadyExisted"/>, even if two requests race), amending to the current terms
/// changes nothing, cancelling a cancelled booking succeeds.</para>
/// </remarks>
public interface IChannelBookings
{
    /// <summary>
    /// Creates the confirmed booking of a channel: driver found again or registered (and the individual payer), price
    /// of the engine frozen as version 1, readable number, all under the availability lock of the category. Quotas
    /// and stop-sells of the channel apply unless <see cref="ChannelBookingRequest.IgnoreChannelRestrictions"/>
    /// (a booking already sold on the extranet: only the physical capacity counts). No vehicle left:
    /// <c>availability.not_available</c>, never an overbooking.
    /// </summary>
    Task<Result<ChannelBookingResult>> CreateConfirmedAsync(
        ChannelBookingRequest request,
        Func<ChannelBookingResult, CancellationToken, Task<Result>>? alongside = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the channel's modification (category, places, dates, extras) to a booking that has not left yet: priced
    /// again at the plan in force as a new quote version, re-checked under the lock when the occupancy moves, reason
    /// <see cref="ChannelBookingReasons.Channel"/>. The same terms again: <see cref="ChannelBookingOutcome.Unchanged"/>.
    /// </summary>
    Task<Result<ChannelBookingResult>> AmendAsync(
        ChannelBookingAmendment amendment,
        Func<ChannelBookingResult, CancellationToken, Task<Result>>? alongside = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the booking for the channel (reason <see cref="ChannelBookingReasons.Channel"/>, no fee of the agency's
    /// policy: the channel's contract governs it) and releases its capacity; a cancelled booking stays cancelled.
    /// </summary>
    Task<Result<ChannelBookingResult>> CancelAsync(
        Guid bookingId,
        Func<ChannelBookingResult, CancellationToken, Task<Result>>? alongside = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The booking of the agency <paramref name="agencyId"/> and of a sales channel carrying
    /// <paramref name="channelReference"/> (exact, trimmed; unique within the agency, ADR-0067), or <see langword="null"/>.
    /// </summary>
    Task<BookingSummaryInfo?> FindByChannelReferenceAsync(Guid agencyId, SalesChannel channel, string channelReference, CancellationToken cancellationToken = default);
}

/// <summary>
/// A booking received from a channel. <see cref="Driver"/> is found again (same phone or e-mail and name) or
/// registered; an invalid phone or e-mail of the channel is left out rather than refusing a sold booking.
/// </summary>
public sealed record ChannelBookingRequest(
    Guid AgencyId,
    SalesChannel Channel,
    string ChannelReference,
    Guid CategoryId,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    ChannelDriverInfo Driver,
    IReadOnlyList<SelectedExtra>? Extras = null,
    string? Notes = null,
    bool IgnoreChannelRestrictions = false);

/// <summary>The main driver as the channel knows it (no document: they are checked at the counter).</summary>
public sealed record ChannelDriverInfo(string FirstName, string LastName, string? Phone = null, string? Email = null);

/// <summary>The new terms of a channel booking (the current extras are kept when <see cref="Extras"/> is <see langword="null"/>).</summary>
public sealed record ChannelBookingAmendment(
    Guid BookingId,
    Guid CategoryId,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    IReadOnlyList<SelectedExtra>? Extras = null,
    bool IgnoreChannelRestrictions = false);

/// <summary>What a write of <see cref="IChannelBookings"/> did.</summary>
public enum ChannelBookingOutcome
{
    Created = 1,

    /// <summary>The reference already had its booking: nothing was created.</summary>
    AlreadyExisted = 2,

    Amended = 3,

    /// <summary>The modification carried the current terms: nothing changed.</summary>
    Unchanged = 4,

    Cancelled = 5,

    AlreadyCancelled = 6,
}

/// <summary>The booking after a write of <see cref="IChannelBookings"/>.</summary>
public sealed record ChannelBookingResult(
    Guid BookingId,
    string Number,
    Guid AgencyId,
    BookingStatusInfo Status,
    string? ChannelReference,
    Guid CategoryId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    Money TotalInclVat,
    ChannelBookingOutcome Outcome);

/// <summary>Reasons the Bookings module records for a channel (keys translated by the back office, resource <c>Reason.{key}</c>).</summary>
public static class ChannelBookingReasons
{
    /// <summary>Modified or cancelled by the channel.</summary>
    public const string Channel = "booking.reason.channel";
}

/// <summary>Error codes of <see cref="IChannelBookings"/> that the Channels module translates into refusals (stable).</summary>
public static class ChannelBookingErrorCodes
{
    /// <summary>No vehicle left for the period on the channel (capacity, quota or stop-sell).</summary>
    public const string NotAvailable = "availability.not_available";

    public const string NotFound = "booking.not_found";

    /// <summary>The booking is in a status that does not allow the operation (e.g. already left).</summary>
    public const string InvalidTransition = "booking.invalid_transition";
}
