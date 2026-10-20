using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;

namespace CarRental.Application.Payments;

/// <summary>Conversions between the domain types of the module and its contract types (same enum values), and totals.</summary>
internal static class PaymentMapping
{
    public static PaymentMethod ToDomain(this PaymentMethodInfo method) => (PaymentMethod)(int)method;

    public static PaymentMethodInfo ToInfo(this PaymentMethod method) => (PaymentMethodInfo)(int)method;

    public static PaymentStatus ToDomain(this PaymentStatusInfo status) => (PaymentStatus)(int)status;

    public static PaymentStatusInfo ToInfo(this PaymentStatus status) => (PaymentStatusInfo)(int)status;

    public static PaymentDirection ToDomain(this PaymentDirectionInfo direction) => (PaymentDirection)(int)direction;

    public static PaymentDirectionInfo ToInfo(this PaymentDirection direction) => (PaymentDirectionInfo)(int)direction;

    public static DepositMode ToDomain(this DepositModeInfo mode) => (DepositMode)(int)mode;

    public static DepositModeInfo ToInfo(this DepositMode mode) => (DepositModeInfo)(int)mode;

    public static DepositStatus ToDomain(this DepositStatusInfo status) => (DepositStatus)(int)status;

    public static DepositStatusInfo ToInfo(this DepositStatus status) => (DepositStatusInfo)(int)status;

    public static PaymentInfo ToInfo(this Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        return new PaymentInfo(
            payment.Id,
            payment.AgencyId,
            payment.BookingId,
            payment.Direction.ToInfo(),
            payment.Method.ToInfo(),
            payment.Status.ToInfo(),
            payment.Amount,
            payment.CapturedAmount,
            payment.RefundedAmount,
            payment.RefundableAmount,
            payment.OriginalPaymentId,
            payment.ExternalReference,
            payment.Notes,
            payment.CashSessionId,
            payment.RecordedAtUtc,
            payment.RecordedByName,
            payment.CapturedAtUtc,
            payment.StatusChangedAtUtc,
            payment.StatusChangedByName,
            payment.StatusReason);
    }

    public static DepositInfo ToInfo(this DepositHold deposit)
    {
        ArgumentNullException.ThrowIfNull(deposit);
        return new DepositInfo(
            deposit.Id,
            deposit.AgencyId,
            deposit.BookingId,
            deposit.Mode.ToInfo(),
            deposit.Status.ToInfo(),
            deposit.Amount,
            deposit.RetainedAmount,
            deposit.ReturnedAmount,
            deposit.OpenLiability,
            deposit.ExternalReference,
            deposit.Notes,
            deposit.RetentionJustification,
            deposit.AttachmentReference,
            deposit.TakenInCashSessionId,
            deposit.SettledInCashSessionId,
            deposit.TakenAtUtc,
            deposit.TakenByName,
            deposit.SettledAtUtc,
            deposit.SettledByName);
    }

    /// <summary>
    /// Totals of payments: captured = incoming money received (captured, or captured then refunded in full); refunded =
    /// refunds paid back; pending and pre-authorized = incoming money not received yet.
    /// </summary>
    public static PaymentTotals TotalsOf(IEnumerable<PaymentAmounts> payments)
    {
        ArgumentNullException.ThrowIfNull(payments);
        var captured = Money.Zero;
        var refunded = Money.Zero;
        var pending = Money.Zero;
        var preAuthorized = Money.Zero;
        foreach (var payment in payments)
        {
            switch (payment.Direction, payment.Status)
            {
                case (PaymentDirection.Incoming, PaymentStatus.Captured or PaymentStatus.Refunded):
                    captured += payment.CapturedAmount;
                    break;
                case (PaymentDirection.Incoming, PaymentStatus.Pending):
                    pending += payment.Amount;
                    break;
                case (PaymentDirection.Incoming, PaymentStatus.PreAuthorized):
                    preAuthorized += payment.Amount;
                    break;
                case (PaymentDirection.Refund, PaymentStatus.Captured):
                    refunded += payment.CapturedAmount;
                    break;
            }
        }

        return new PaymentTotals(captured, refunded, captured - refunded, pending, preAuthorized);
    }

    /// <summary>Totals of deposits: taken (all), still owed back (held), retained and returned (settled).</summary>
    public static DepositTotals TotalsOf(IEnumerable<DepositAmounts> deposits)
    {
        ArgumentNullException.ThrowIfNull(deposits);
        var taken = Money.Zero;
        var open = Money.Zero;
        var retained = Money.Zero;
        var returned = Money.Zero;
        var held = 0;
        foreach (var deposit in deposits)
        {
            taken += deposit.Amount;
            if (deposit.Status == DepositStatus.Held)
            {
                open += deposit.Amount;
                held++;
            }
            else
            {
                retained += deposit.RetainedAmount;
                returned += deposit.Amount - deposit.RetainedAmount;
            }
        }

        return new DepositTotals(taken, open, retained, returned, held);
    }

    public static PaymentAmounts AmountsOf(this Payment payment) => new(payment.Direction, payment.Status, payment.Amount, payment.CapturedAmount);

    public static DepositAmounts AmountsOf(this DepositHold deposit) => new(deposit.Status, deposit.Amount, deposit.RetainedAmount);
}

/// <summary>What the payment totals need of a payment (projected by the queries).</summary>
internal sealed record PaymentAmounts(PaymentDirection Direction, PaymentStatus Status, Money Amount, Money CapturedAmount);

/// <summary>What the deposit totals need of a deposit (projected by the queries).</summary>
internal sealed record DepositAmounts(DepositStatus Status, Money Amount, Money RetainedAmount);

/// <summary>Conversions of form input (decimal amounts typed in dinars) into domain values.</summary>
internal static class PaymentInput
{
    /// <summary>A positive amount in TND with three decimals at most (1.2345 is rejected, not rounded).</summary>
    public static Result<Money> Amount(decimal amount)
    {
        var money = Money.FromExact(amount, Currency.Tnd);
        return money.IsSuccess && money.Value.IsPositive && amount <= Payment.MaxAmount ? money.Value : PaymentsErrors.AmountInvalid;
    }
}
