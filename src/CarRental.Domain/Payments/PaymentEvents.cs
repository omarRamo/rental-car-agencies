using CarRental.Domain.Common;

namespace CarRental.Domain.Payments;

// Events carry identifiers and discriminators only (no amount, reference or text): handlers read what they need.

/// <summary>A payment (incoming or refund) was recorded with its initial status.</summary>
public sealed record PaymentRecorded(
    Guid PaymentId,
    Guid BookingId,
    Guid AgencyId,
    PaymentDirection Direction,
    PaymentMethod Method,
    PaymentStatus Status) : IDomainEvent;

/// <summary>The status of a payment changed (confirmed, cancelled, pre-authorized, captured, failed, fully refunded).</summary>
public sealed record PaymentStatusChanged(
    Guid PaymentId,
    Guid BookingId,
    PaymentDirection Direction,
    PaymentStatus PreviousStatus,
    PaymentStatus Status) : IDomainEvent;

/// <summary>A refund was recorded against the incoming payment <see cref="PaymentId"/>.</summary>
public sealed record PaymentRefundRecorded(Guid PaymentId, Guid RefundId, Guid BookingId) : IDomainEvent;

/// <summary>A security deposit was taken for a booking.</summary>
public sealed record DepositTaken(Guid DepositId, Guid BookingId, Guid AgencyId, DepositMode Mode) : IDomainEvent;

/// <summary>A security deposit was returned in full.</summary>
public sealed record DepositReleased(Guid DepositId, Guid BookingId) : IDomainEvent;

/// <summary>A security deposit was retained in part (<see cref="DepositStatus.PartiallyRetained"/>) or in full.</summary>
public sealed record DepositRetained(Guid DepositId, Guid BookingId, DepositStatus Status) : IDomainEvent;
