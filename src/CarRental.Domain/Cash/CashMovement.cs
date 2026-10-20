using CarRental.Domain.Common;

namespace CarRental.Domain.Cash;

/// <summary>
/// A line of a <see cref="CashSession"/>: what moved, by how much, its signed effect on each pocket and what caused it.
/// Movements are only appended, numbered within their session (<see cref="Sequence"/>), never changed nor removed.
/// </summary>
public sealed class CashMovement : Entity
{
    public const int ReasonMinLength = 3;
    public const int ReasonMaxLength = 500;

    private CashMovement()
    {
    }

    private CashMovement(Guid id, Guid sessionId, int sequence, PlannedCashMovement planned, AuditActor by, DateTimeOffset at)
        : base(id)
    {
        SessionId = sessionId;
        Sequence = sequence;
        Kind = planned.Kind;
        Amount = planned.Amount;
        RevenueEffect = planned.RevenueEffect;
        DepositEffect = planned.DepositEffect;
        Reason = planned.Reason;
        BookingId = planned.BookingId;
        PaymentId = planned.PaymentId;
        DepositId = planned.DepositId;
        RelatedSessionId = planned.RelatedSessionId;
        RecordedAtUtc = at;
        RecordedByUserId = by.UserId;
        RecordedByName = CashText.ActorName(by);
    }

    public Guid SessionId { get; private set; }

    /// <summary>Order within the session (1, 2, 3…): the movements read in this order, independently of the clock.</summary>
    public int Sequence { get; private set; }

    public CashMovementKind Kind { get; private set; }

    /// <summary>Size of the movement (always positive).</summary>
    public Money Amount { get; private set; }

    /// <summary>Signed effect on the revenue cash.</summary>
    public Money RevenueEffect { get; private set; }

    /// <summary>Signed effect on the deposit cash.</summary>
    public Money DepositEffect { get; private set; }

    /// <summary>Reason of a manual movement or an adjustment.</summary>
    public string? Reason { get; private set; }

    public Guid? BookingId { get; private set; }

    /// <summary>The payment (or refund) received or paid back in cash.</summary>
    public Guid? PaymentId { get; private set; }

    /// <summary>The cash deposit received, returned, retained or transferred.</summary>
    public Guid? DepositId { get; private set; }

    /// <summary>Transfer: the other session of the transfer; adjustment: the corrected session.</summary>
    public Guid? RelatedSessionId { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public Guid? RecordedByUserId { get; private set; }

    public string? RecordedByName { get; private set; }

    internal static CashMovement Create(Guid sessionId, int sequence, PlannedCashMovement planned, AuditActor by, DateTimeOffset at) =>
        new(NewId(), sessionId, sequence, planned, by, at);
}

/// <summary>A movement checked by the session before it is appended.</summary>
internal sealed record PlannedCashMovement(CashMovementKind Kind, Money Amount, Money RevenueEffect, Money DepositEffect)
{
    public string? Reason { get; init; }

    public Guid? BookingId { get; init; }

    public Guid? PaymentId { get; init; }

    public Guid? DepositId { get; init; }

    public Guid? RelatedSessionId { get; init; }

    /// <summary>An automatic transfer out records a fact (the deposit left the drawer): it may leave the pocket negative.</summary>
    public bool MayLeavePocketNegative { get; init; }
}
