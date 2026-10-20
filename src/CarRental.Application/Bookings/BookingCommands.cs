using CarRental.Application.Bookings.Administration;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings;

/// <summary>What a phone or counter booking becomes: a dated option, or a confirmed booking.</summary>
public enum QuickBookingOutcome
{
    Option = 1,
    Confirmed = 2,
}

/// <summary>
/// A booking entered by an agent (phone, counter, WhatsApp conversation, or a Booking.com booking entered by hand).
/// <see cref="PayerCustomerId"/> <see langword="null"/> means the main driver pays (individual customer created when
/// needed); <see cref="OptionExpiresAt"/> <see langword="null"/> means the default option duration of the agency;
/// <see cref="ExpectedTotalInclVat"/> is the total the agent saw: a different price refuses the save.
/// </summary>
public sealed record QuickBookingRequest(
    Guid AgencyId,
    SalesChannel Channel,
    Guid CategoryId,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    Guid MainDriverId,
    QuickBookingOutcome Outcome,
    IReadOnlyList<SelectedExtra>? Extras = null,
    string? PromoCode = null,
    Guid? SecondaryDriverId = null,
    Guid? PayerCustomerId = null,
    DateTimeOffset? OptionExpiresAt = null,
    Guid? GuaranteedVehicleId = null,
    string? ChannelReference = null,
    string? Notes = null,
    Money? ExpectedTotalInclVat = null,
    string? Source = null);

/// <summary>The file created by a quick booking.</summary>
public sealed record QuickBookingResult(Guid BookingId, string Number, BookingStatus Status, Money TotalInclVat, DateTimeOffset? OptionExpiresAt);

/// <summary>What the validation of a web request did.</summary>
public enum ValidationOutcome
{
    /// <summary>The request is confirmed (normal payment mode).</summary>
    Confirmed = 1,

    /// <summary>The request waits for its payment by link (fallback payment mode).</summary>
    AwaitingPayment = 2,

    /// <summary>The internal customer check failed: the request was refused with a generic reason.</summary>
    RefusedAfterCustomerCheck = 3,
}

/// <summary>The fee a cancellation would cost now, and until when it is free.</summary>
public sealed record CancellationPreview(Money Fee, DateOnly FreeUntil, decimal FeePercent, bool IsFree);

/// <summary>
/// An amendment of a file: the new value of every amendable field (start from the current values), an optional manual
/// discount (TTC, with its mandatory reason; permission <c>bookings.discount</c>) and the total the agent saw.
/// <see cref="PayerCustomerId"/> <see langword="null"/> means the main driver pays.
/// </summary>
public sealed record AmendBookingRequest(
    Guid BookingId,
    Guid CategoryId,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    IReadOnlyList<SelectedExtra> Extras,
    string? PromoCode,
    Guid? GuaranteedVehicleId,
    Guid MainDriverId,
    Guid? SecondaryDriverId,
    Guid? PayerCustomerId,
    string? Notes,
    bool ReleaseVehicle = false,
    Money? ManualDiscount = null,
    string? Reason = null,
    Money? ExpectedTotalInclVat = null);

/// <summary>The price an amendment would give, before it is saved.</summary>
public sealed record AmendmentPreview(
    bool IsExtension,
    bool CreatesQuoteVersion,
    Money CurrentTotalInclVat,
    Money NewTotalInclVat,
    QuoteView? NewQuote,
    bool? IsAvailable);

/// <summary>The outcome of an amendment: the current quote version and total afterwards.</summary>
public sealed record AmendmentResult(int QuoteVersion, Money TotalInclVat);

/// <summary>An assignment proposed by the greedy assigner (ADR-0025), with what the agent needs to review it.</summary>
public sealed record AssignmentProposalView(
    Guid BookingId,
    string BookingNumber,
    Guid CategoryId,
    string CategoryCode,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    Guid VehicleId,
    string PlateNumber);

/// <summary>A file the greedy assigner could not place, or a fixed assignment that breaks a rule, with the error.</summary>
public sealed record AssignmentIssueView(Guid BookingId, string BookingNumber, string CategoryCode, Guid? VehicleId, string? PlateNumber, Error Reason);

/// <summary>Proposals of the greedy assigner over a range, to review before applying.</summary>
public sealed record AssignmentPlanView(
    Period Range,
    IReadOnlyList<AssignmentProposalView> Proposals,
    IReadOnlyList<AssignmentIssueView> Unassignable,
    IReadOnlyList<AssignmentIssueView> Conflicts);

/// <summary>An assignment to apply (from a reviewed proposal).</summary>
public sealed record AssignmentChoice(Guid BookingId, Guid VehicleId);

/// <summary>The outcome of one applied assignment.</summary>
public sealed record AssignmentOutcome(Guid BookingId, Guid VehicleId, Error? Error)
{
    public bool IsSuccess => Error is null;
}
