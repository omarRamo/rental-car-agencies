using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using static CarRental.Domain.Tests.Payments.PaymentTestData;

namespace CarRental.Domain.Tests.Payments;

/// <summary>Money invariants of payments and deposits, on thousands of random amounts and refund sequences.</summary>
public sealed class PaymentPropertyTests
{
    /// <summary>Amounts in millimes, from 0.001 to 5 000 TND.</summary>
    private static Gen<decimal> Millimes(int maximum = 5_000_000) => Gen.Choose(1, maximum).Select(m => m / 1000m);

    [Property(MaxTest = 1000)]
    public Property Refunds_never_exceed_what_was_captured()
    {
        var scenario =
            from captured in Millimes()
            from count in Gen.Choose(0, 12)
            from attempts in Gen.ArrayOf(Millimes(2_000_000), count)
            select (captured, attempts);

        return Prop.ForAll(scenario.ToArbitrary(), s =>
        {
            var payment = Captured(PaymentMethod.CardTerminal, s.captured);
            var accepted = Money.Zero;
            foreach (var attempt in s.attempts)
            {
                var refundable = payment.RefundableAmount;
                var refund = payment.RecordRefund(CardRefund(attempt), Agent, Now);
                if (refund.IsSuccess != (Tnd(attempt) <= refundable))
                {
                    return false;
                }

                if (refund.IsSuccess)
                {
                    accepted += refund.Value.Amount;
                }
            }

            return payment.RefundedAmount == accepted
                && payment.RefundedAmount <= payment.CapturedAmount
                && payment.RefundableAmount == (payment.Status == PaymentStatus.Captured ? payment.CapturedAmount - accepted : Money.Zero)
                && (payment.Status == PaymentStatus.Refunded) == (accepted == payment.CapturedAmount);
        });
    }

    [Property(MaxTest = 1000)]
    public Property A_settled_deposit_splits_into_returned_and_retained_and_owes_nothing_more()
    {
        var scenario =
            from amount in Millimes()
            from share in Gen.Choose(0, 1000)
            select (amount, share);

        return Prop.ForAll(scenario.ToArbitrary(), s =>
        {
            var deposit = DepositHold.Take(Deposit(DepositMode.CardImprint, s.amount), Agent, Now).Value;
            var retained = (Tnd(s.amount) * (s.share / 1000m)).Amount;
            var settled = retained == 0m
                ? deposit.Release(null, Agent, Now)
                : deposit.Retain(Tnd(retained), "Dommages constatés", null, null, Agent, Now);

            return settled.IsSuccess
                && deposit.OpenLiability == Money.Zero
                && deposit.RetainedAmount + deposit.ReturnedAmount == deposit.Amount
                && deposit.Status == (retained == 0m ? DepositStatus.Released
                    : retained == deposit.Amount.Amount ? DepositStatus.Retained
                    : DepositStatus.PartiallyRetained);
        });
    }
}
