using CarRental.Domain.Common;

namespace CarRental.Domain.Cash;

/// <summary>
/// The cash drawer of an agent for a business day in an agency (aggregate, SPEC §8 "Caisse"): opening float, typed
/// movements, expected cash computed separately for the revenue cash and the deposit cash (cash deposits are owed to
/// customers and counted apart), closing count with justified variances, then lock by a manager or the accountant.
/// </summary>
/// <remarks>
/// <para>One session per cashier, agency and Tunis business day (the day it was opened), and at most one open session
/// per cashier: checked by <see cref="CashSessionRules"/> and guaranteed by unique indexes. A session stays open until its
/// cashier closes it, even after midnight (late returns): the business date is the day of the opening.</para>
/// <para>Outflows never take a pocket below zero: an agent cannot pay out cash the drawer does not hold. The single
/// exception is an automatic transfer out, which records a fact.</para>
/// <para>A closed session accepts nothing but its lock; a locked session never changes. An error found later is corrected
/// by an <see cref="CashMovementKind.Adjustment"/> in a later open session, referencing the corrected session.</para>
/// <para>Every movement updates the root (expected cash, <see cref="LastSequence"/>), so the <c>rowversion</c> of the
/// root guards the whole session against concurrent changes.</para>
/// </remarks>
public sealed class CashSession : AggregateRoot
{
    public const int JustificationMinLength = 3;
    public const int JustificationMaxLength = 500;

    /// <summary>Upper bound of one amount, in TND (guard against typing errors).</summary>
    public const decimal MaxAmount = 1_000_000m;

    private readonly List<CashMovement> _movements = [];

    private CashSession()
    {
    }

    private CashSession(Guid id, Guid agencyId, Guid cashierId, string cashierName, Money openingFloat, DateTimeOffset at)
        : base(id)
    {
        AgencyId = agencyId;
        CashierId = cashierId;
        CashierName = cashierName;
        BusinessDate = CashSessionRules.BusinessDateOf(at);
        Status = CashSessionStatus.Open;
        OpeningFloat = openingFloat;
        ExpectedRevenueCash = openingFloat;
        ExpectedDepositCash = Money.Zero;
        OpenedAtUtc = at;
    }

    public Guid AgencyId { get; private set; }

    /// <summary>The user who owns the drawer.</summary>
    public Guid CashierId { get; private set; }

    /// <summary>Display name of the cashier when the session was opened.</summary>
    public string CashierName { get; private set; } = null!;

    /// <summary>Tunis calendar day of the opening.</summary>
    public DateOnly BusinessDate { get; private set; }

    public CashSessionStatus Status { get; private set; }

    /// <summary>Fonds de caisse: revenue cash in the drawer at the opening.</summary>
    public Money OpeningFloat { get; private set; }

    /// <summary>Revenue cash the drawer should hold: opening float plus every revenue effect.</summary>
    public Money ExpectedRevenueCash { get; private set; }

    /// <summary>Deposit cash the drawer should hold: every deposit effect.</summary>
    public Money ExpectedDepositCash { get; private set; }

    public Money? CountedRevenueCash { get; private set; }

    public Money? CountedDepositCash { get; private set; }

    /// <summary>Counted minus expected revenue cash (negative: missing cash).</summary>
    public Money? RevenueVariance { get; private set; }

    /// <summary>Counted minus expected deposit cash.</summary>
    public Money? DepositVariance { get; private set; }

    /// <summary>Explanation of the variances (mandatory when one is not zero).</summary>
    public string? VarianceJustification { get; private set; }

    public DateTimeOffset OpenedAtUtc { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    public Guid? ClosedByUserId { get; private set; }

    public string? ClosedByName { get; private set; }

    public DateTimeOffset? LockedAtUtc { get; private set; }

    public Guid? LockedByUserId { get; private set; }

    public string? LockedByName { get; private set; }

    /// <summary>Sequence number of the last movement.</summary>
    public int LastSequence { get; private set; }

    public IReadOnlyList<CashMovement> Movements => _movements;

    public bool IsOpen => Status == CashSessionStatus.Open;

    /// <summary>True once closed with a non-zero variance on a pocket.</summary>
    public bool HasVariance => RevenueVariance is { IsZero: false } || DepositVariance is { IsZero: false };

    /// <summary>
    /// Opens the drawer of <paramref name="cashierId"/> with its opening float. The business date is the Tunis day of
    /// <paramref name="at"/>. Check <see cref="CashSessionRules.EnsureCanOpen"/> first.
    /// </summary>
    public static Result<CashSession> Open(Guid agencyId, Guid cashierId, string? cashierName, Money openingFloat, DateTimeOffset at)
    {
        if (agencyId == Guid.Empty)
        {
            return CashErrors.AgencyRequired;
        }

        if (cashierId == Guid.Empty)
        {
            return CashErrors.CashierRequired;
        }

        if (!CashText.IsCountableAmount(openingFloat))
        {
            return CashErrors.OpeningFloatInvalid;
        }

        var name = CashText.Name(cashierName) ?? cashierId.ToString();
        var session = new CashSession(NewId(), agencyId, cashierId, name, openingFloat, at);
        session.Raise(new CashSessionOpened(session.Id, agencyId, cashierId));
        return session;
    }

    /// <summary>Cash payment received from a customer (revenue +).</summary>
    public Result<CashMovement> RecordPaymentReceived(Guid paymentId, Guid bookingId, Money amount, AuditActor by, DateTimeOffset at) =>
        RecordOne(new PlannedCashMovement(CashMovementKind.PaymentReceived, amount, amount, Money.Zero) { PaymentId = paymentId, BookingId = bookingId }, by, at);

    /// <summary>Cash refund paid to a customer (revenue −).</summary>
    public Result<CashMovement> RecordRefundPaid(Guid refundId, Guid bookingId, Money amount, AuditActor by, DateTimeOffset at) =>
        RecordOne(new PlannedCashMovement(CashMovementKind.RefundPaid, amount, -amount, Money.Zero) { PaymentId = refundId, BookingId = bookingId }, by, at);

    /// <summary>Cash deposit received (deposit +).</summary>
    public Result<CashMovement> RecordDepositReceived(Guid depositId, Guid bookingId, Money amount, AuditActor by, DateTimeOffset at) =>
        RecordOne(new PlannedCashMovement(CashMovementKind.DepositReceived, amount, Money.Zero, amount) { DepositId = depositId, BookingId = bookingId }, by, at);

    /// <summary>
    /// Settles a cash deposit from this drawer: the unretained part goes back to the customer (deposit −), the retained
    /// part becomes revenue cash (deposit −, revenue +). A deposit received in another session is first brought into
    /// this drawer (<see cref="CashMovementKind.DepositTransferIn"/>: from the safe or from the drawer of the agent who
    /// received it). All the movements are recorded together, or none.
    /// </summary>
    public Result<IReadOnlyList<CashMovement>> RecordDepositSettlement(DepositCashSettlement settlement, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        if (!CashText.IsPositiveAmount(settlement.Amount)
            || settlement.Retained.Currency != Currency.Tnd
            || settlement.Retained.IsNegative
            || settlement.Retained > settlement.Amount)
        {
            return CashErrors.AmountInvalid;
        }

        var planned = new List<PlannedCashMovement>();
        if (settlement.ReceivedInSessionId != Id)
        {
            planned.Add(new PlannedCashMovement(CashMovementKind.DepositTransferIn, settlement.Amount, Money.Zero, settlement.Amount)
            {
                DepositId = settlement.DepositId,
                BookingId = settlement.BookingId,
                RelatedSessionId = settlement.ReceivedInSessionId,
            });
        }

        var returned = settlement.Amount - settlement.Retained;
        if (returned.IsPositive)
        {
            planned.Add(new PlannedCashMovement(CashMovementKind.DepositReturned, returned, Money.Zero, -returned)
            {
                DepositId = settlement.DepositId,
                BookingId = settlement.BookingId,
            });
        }

        if (settlement.Retained.IsPositive)
        {
            planned.Add(new PlannedCashMovement(CashMovementKind.DepositRetained, settlement.Retained, settlement.Retained, -settlement.Retained)
            {
                DepositId = settlement.DepositId,
                BookingId = settlement.BookingId,
            });
        }

        return Record(planned, by, at);
    }

    /// <summary>
    /// A cash deposit received in this open session leaves the drawer to be settled by the agent of session
    /// <paramref name="toSessionId"/> (deposit −). Automatic, it records a fact and may leave the pocket negative.
    /// </summary>
    public Result<CashMovement> RecordDepositHandedOver(Guid depositId, Guid bookingId, Money amount, Guid toSessionId, AuditActor by, DateTimeOffset at) =>
        RecordOne(
            new PlannedCashMovement(CashMovementKind.DepositTransferOut, amount, Money.Zero, -amount)
            {
                DepositId = depositId,
                BookingId = bookingId,
                RelatedSessionId = toSessionId,
                MayLeavePocketNegative = true,
            },
            by,
            at);

    /// <summary>Other cash put in or taken from the revenue cash, with a mandatory reason.</summary>
    public Result<CashMovement> AddManualMovement(CashFlowDirection direction, Money amount, string? reason, AuditActor by, DateTimeOffset at)
    {
        if (!Enum.IsDefined(direction))
        {
            return CashErrors.DirectionInvalid;
        }

        var text = CashText.Reason(reason);
        if (text.IsFailure)
        {
            return text.Error;
        }

        var planned = direction == CashFlowDirection.In
            ? new PlannedCashMovement(CashMovementKind.ManualIn, amount, amount, Money.Zero)
            : new PlannedCashMovement(CashMovementKind.ManualOut, amount, -amount, Money.Zero);
        return RecordOne(planned with { Reason = text.Value }, by, at);
    }

    /// <summary>
    /// Corrects an error found in an earlier session (usually locked) by a signed amount on one pocket, with a mandatory
    /// reason; <paramref name="correctedSessionId"/> names the corrected session when known.
    /// </summary>
    public Result<CashMovement> AddAdjustment(CashPocket pocket, Money amount, string? reason, Guid? correctedSessionId, AuditActor by, DateTimeOffset at)
    {
        if (!Enum.IsDefined(pocket))
        {
            return CashErrors.PocketInvalid;
        }

        if (amount.Currency != Currency.Tnd || amount.IsZero || Math.Abs(amount.Amount) > MaxAmount)
        {
            return CashErrors.AmountInvalid;
        }

        if (correctedSessionId == Id || correctedSessionId == Guid.Empty)
        {
            return CashErrors.CorrectedSessionInvalid;
        }

        var text = CashText.Reason(reason);
        if (text.IsFailure)
        {
            return text.Error;
        }

        var planned = new PlannedCashMovement(
            CashMovementKind.Adjustment,
            amount.Abs(),
            pocket == CashPocket.Revenue ? amount : Money.Zero,
            pocket == CashPocket.Deposit ? amount : Money.Zero)
        {
            Reason = text.Value,
            RelatedSessionId = correctedSessionId,
        };
        return RecordOne(planned, by, at);
    }

    /// <summary>
    /// Closes the session with the counted cash of each pocket. Variances (counted − expected) are stored; a non-zero
    /// variance requires a justification.
    /// </summary>
    public Result Close(Money countedRevenueCash, Money countedDepositCash, string? justification, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (!CashText.IsCountableAmount(countedRevenueCash) || !CashText.IsCountableAmount(countedDepositCash))
        {
            return CashErrors.CountedAmountInvalid;
        }

        var revenueVariance = countedRevenueCash - ExpectedRevenueCash;
        var depositVariance = countedDepositCash - ExpectedDepositCash;
        var text = CashText.Clean(justification);
        if (text is { Length: > JustificationMaxLength })
        {
            return CashErrors.JustificationTooLong;
        }

        if ((!revenueVariance.IsZero || !depositVariance.IsZero) && (text is null || text.Length < JustificationMinLength))
        {
            return CashErrors.VarianceJustificationRequired;
        }

        CountedRevenueCash = countedRevenueCash;
        CountedDepositCash = countedDepositCash;
        RevenueVariance = revenueVariance;
        DepositVariance = depositVariance;
        VarianceJustification = text;
        Status = CashSessionStatus.Closed;
        ClosedAtUtc = at;
        ClosedByUserId = by.UserId;
        ClosedByName = CashText.ActorName(by);
        Raise(new CashSessionClosed(Id, AgencyId, CashierId, HasVariance));
        return Result.Success();
    }

    /// <summary>Locks a closed session (manager or accountant): it never changes afterwards.</summary>
    public Result Lock(AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (Status == CashSessionStatus.Locked)
        {
            return CashErrors.SessionLocked;
        }

        if (Status != CashSessionStatus.Closed)
        {
            return CashErrors.SessionNotClosed;
        }

        Status = CashSessionStatus.Locked;
        LockedAtUtc = at;
        LockedByUserId = by.UserId;
        LockedByName = CashText.ActorName(by);
        Raise(new CashSessionLocked(Id, AgencyId, CashierId));
        return Result.Success();
    }

    private static bool SameSettlementGroup(CashMovementKind existing, CashMovementKind planned)
    {
        static bool IsSettlement(CashMovementKind kind) =>
            kind is CashMovementKind.DepositTransferIn or CashMovementKind.DepositReturned or CashMovementKind.DepositRetained;

        return existing == planned || (IsSettlement(existing) && IsSettlement(planned));
    }

    private Result EnsureOpen() => Status switch
    {
        CashSessionStatus.Open => Result.Success(),
        CashSessionStatus.Closed => CashErrors.SessionClosed,
        _ => CashErrors.SessionLocked,
    };

    private Result<CashMovement> RecordOne(PlannedCashMovement planned, AuditActor by, DateTimeOffset at)
    {
        var recorded = Record([planned], by, at);
        return recorded.IsSuccess ? recorded.Value[0] : recorded.Error;
    }

    /// <summary>Checks every planned movement against the session, then appends them all (or none).</summary>
    private Result<IReadOnlyList<CashMovement>> Record(List<PlannedCashMovement> planned, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open.Error;
        }

        var revenue = ExpectedRevenueCash;
        var deposit = ExpectedDepositCash;
        foreach (var movement in planned)
        {
            if (!CashText.IsPositiveAmount(movement.Amount))
            {
                return CashErrors.AmountInvalid;
            }

            if (IsDuplicate(movement))
            {
                return CashErrors.DuplicateMovement;
            }

            revenue += movement.RevenueEffect;
            deposit += movement.DepositEffect;
            if (!movement.MayLeavePocketNegative && (revenue.IsNegative || deposit.IsNegative))
            {
                return CashErrors.InsufficientCash;
            }
        }

        var appended = new List<CashMovement>(planned.Count);
        foreach (var movement in planned)
        {
            LastSequence++;
            var line = CashMovement.Create(Id, LastSequence, movement, by, at);
            _movements.Add(line);
            ExpectedRevenueCash += movement.RevenueEffect;
            ExpectedDepositCash += movement.DepositEffect;
            appended.Add(line);
            Raise(new CashMovementRecorded(Id, line.Id, line.Kind));
        }

        return appended;
    }

    /// <summary>A payment is recorded once per kind; a deposit is received once, settled once and handed over once.</summary>
    private bool IsDuplicate(PlannedCashMovement planned) =>
        (planned.PaymentId is { } paymentId && _movements.Any(m => m.PaymentId == paymentId && m.Kind == planned.Kind))
        || (planned.DepositId is { } depositId && _movements.Any(m => m.DepositId == depositId && SameSettlementGroup(m.Kind, planned.Kind)));
}

/// <summary>How a cash deposit is settled from a drawer.</summary>
/// <param name="DepositId">The deposit settled.</param>
/// <param name="BookingId">Its booking.</param>
/// <param name="Amount">Whole amount of the deposit.</param>
/// <param name="Retained">Part kept by the agency (zero for a release).</param>
/// <param name="ReceivedInSessionId">Session in which the deposit was received.</param>
public sealed record DepositCashSettlement(Guid DepositId, Guid BookingId, Money Amount, Money Retained, Guid ReceivedInSessionId);
