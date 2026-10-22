using CarRental.Application.Cash.Contracts;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;

namespace CarRental.Application.Cash;

/// <summary>Conversions between the domain types of the module and its contract types (same enum values).</summary>
internal static class CashMapping
{
    public static CashSessionStatusInfo ToInfo(this CashSessionStatus status) => (CashSessionStatusInfo)(int)status;

    public static CashSessionStatus ToDomain(this CashSessionStatusInfo status) => (CashSessionStatus)(int)status;

    public static CashMovementKindInfo ToInfo(this CashMovementKind kind) => (CashMovementKindInfo)(int)kind;

    public static CashPocket ToDomain(this CashPocketInfo pocket) => (CashPocket)(int)pocket;

    public static CashFlowDirection ToDomain(this CashFlowDirectionInfo direction) => (CashFlowDirection)(int)direction;

    /// <summary>The session with its movements in order and the expected cash of each pocket after every movement.</summary>
    public static CashSessionDetails ToDetails(this CashSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var revenue = session.OpeningFloat;
        var deposit = Money.Zero;
        var movements = new List<CashMovementInfo>(session.Movements.Count);
        foreach (var movement in session.Movements.OrderBy(m => m.Sequence))
        {
            revenue += movement.RevenueEffect;
            deposit += movement.DepositEffect;
            movements.Add(new CashMovementInfo(
                movement.Id,
                movement.Sequence,
                movement.Kind.ToInfo(),
                movement.Amount,
                movement.RevenueEffect,
                movement.DepositEffect,
                revenue,
                deposit,
                movement.Reason,
                movement.BookingId,
                movement.PaymentId,
                movement.DepositId,
                movement.RelatedSessionId,
                movement.RecordedAtUtc,
                movement.RecordedByName,
                movement.ExpenseId));
        }

        return new CashSessionDetails(
            session.Id,
            session.AgencyId,
            session.CashierId,
            session.CashierName,
            session.BusinessDate,
            session.Status.ToInfo(),
            session.OpeningFloat,
            session.ExpectedRevenueCash,
            session.ExpectedDepositCash,
            session.CountedRevenueCash,
            session.CountedDepositCash,
            session.RevenueVariance,
            session.DepositVariance,
            session.VarianceJustification,
            session.OpenedAtUtc,
            session.ClosedAtUtc,
            session.ClosedByName,
            session.LockedAtUtc,
            session.LockedByName,
            TotalsOf(session.Movements),
            movements);
    }

    public static CashMovementTotals TotalsOf(IReadOnlyCollection<CashMovement> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);
        Money SumOf(CashMovementKind kind) => movements.Where(m => m.Kind == kind).Select(m => m.Amount).Sum();
        var adjustments = movements.Where(m => m.Kind == CashMovementKind.Adjustment).ToList();
        return new CashMovementTotals(
            SumOf(CashMovementKind.PaymentReceived),
            SumOf(CashMovementKind.RefundPaid),
            SumOf(CashMovementKind.DepositReceived),
            SumOf(CashMovementKind.DepositReturned),
            SumOf(CashMovementKind.DepositRetained),
            SumOf(CashMovementKind.DepositTransferIn),
            SumOf(CashMovementKind.DepositTransferOut),
            SumOf(CashMovementKind.ManualIn),
            SumOf(CashMovementKind.ManualOut),
            adjustments.Select(m => m.RevenueEffect).Sum(),
            adjustments.Select(m => m.DepositEffect).Sum(),
            SumOf(CashMovementKind.ExpensePaid),
            SumOf(CashMovementKind.ExpenseReversed));
    }
}

/// <summary>Conversions of form input (decimal amounts typed in dinars) into domain values.</summary>
internal static class CashInput
{
    /// <summary>A positive amount in TND with three decimals at most.</summary>
    public static Result<Money> Positive(decimal amount)
    {
        var money = Money.FromExact(amount, Currency.Tnd);
        return money.IsSuccess && money.Value.IsPositive && amount <= CashSession.MaxAmount ? money.Value : CashErrors.AmountInvalid;
    }

    /// <summary>A zero or positive amount in TND with three decimals at most (float, counted cash).</summary>
    public static Result<Money> Countable(decimal amount, Error invalid)
    {
        var money = Money.FromExact(amount, Currency.Tnd);
        return money.IsSuccess && !money.Value.IsNegative && amount <= CashSession.MaxAmount ? money.Value : invalid;
    }

    /// <summary>A signed, non-zero amount in TND with three decimals at most (adjustment).</summary>
    public static Result<Money> Signed(decimal amount)
    {
        var money = Money.FromExact(amount, Currency.Tnd);
        return money.IsSuccess && !money.Value.IsZero && Math.Abs(amount) <= CashSession.MaxAmount ? money.Value : CashErrors.AmountInvalid;
    }
}
