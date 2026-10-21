using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Customers.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.PublicAccess;

/// <summary>Why an access request was not taken (format only: an unknown booking gets the same answer as a known one).</summary>
public enum AccessRequestProblem
{
    EmailInvalid = 1,
    NumberInvalid = 2,
}

/// <summary>
/// The answer to an access request, identical whether a booking matched or not (<see cref="RequestId"/> identifies the
/// request, to type the code), or the format problems of what was typed.
/// </summary>
public sealed record AccessRequestOutcome(Guid? RequestId, IReadOnlyList<AccessRequestProblem> Problems)
{
    public bool IsAccepted => RequestId is not null;
}

/// <summary>An open session of "Ma réservation": the grant that carries it, its booking and its end (sliding).</summary>
public sealed record MyBookingTicket(Guid GrantId, Guid BookingId, DateTimeOffset ExpiresAtUtc, AccessPurpose Purpose, string Culture);

/// <summary>A row of the public timeline of a booking (what the customer may see: no internal step).</summary>
public sealed record MyBookingTimelineEntry(BookingEventType Type, DateTimeOffset OccurredAtUtc);

/// <summary>A photo slot of a driver in "Ma réservation" and whether it was sent for this booking.</summary>
public sealed record MyBookingSlot(string Key, FunnelDocument Document, FunnelSide Side, bool Received, bool Optional);

/// <summary>A driver of the booking (names as typed with the request, or on file for an agent's booking) and its photo slots.</summary>
public sealed record MyBookingDriver(string Role, string Name, IdentityDocumentTypeInfo IdentityType, IReadOnlyList<MyBookingSlot> Slots);

/// <summary>What cancelling now would cost, and what would be refunded (online payments only).</summary>
public sealed record MyBookingCancellation(Money Fee, DateOnly? FreeUntil, decimal FeePercent, bool IsFree, Money RefundEstimate);

/// <summary>A booking as its customer sees it in "Ma réservation".</summary>
public sealed record MyBookingView(
    Guid BookingId,
    string Number,
    BookingStatusInfo Status,
    SalesChannel Channel,
    Guid AgencyId,
    Guid CategoryId,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    Money TotalInclVat,
    Money Deposit,
    Money Paid,
    Money Balance,
    DateTimeOffset? HoldExpiresAt,
    DateTimeOffset? PaymentDueAt,
    string? InformationMessage,
    RequestPaymentState PaymentState,
    IReadOnlyList<MyBookingTimelineEntry> Timeline,
    IReadOnlyList<MyBookingDriver> Drivers,
    bool CanSendDocuments,
    bool CanProvideInformation,
    MyBookingCancellation? Cancellation,
    bool CanPayBalance,
    IReadOnlyList<CustomerDocumentInfo> Documents,
    bool DocumentsAvailable,
    string Culture);

/// <summary>The outcome of a cancellation from "Ma réservation".</summary>
public sealed record MyBookingCancelled(Money Fee, Money RefundAmount, CancellationRefundState Refund);
