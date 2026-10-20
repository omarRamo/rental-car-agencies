using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using static CarRental.Domain.Tests.Cash.CashTestData;

namespace CarRental.Domain.Tests.Cash;

/// <summary>
/// Expected cash on thousands of random days of counter work, against a naive model: revenue and deposit cash match
/// the model, never go negative, equal the float plus the effects of the movements, and a rejected operation changes
/// nothing.
/// </summary>
public sealed class CashSessionPropertyTests
{
    private enum OperationKind
    {
        Payment,
        Refund,
        ManualIn,
        ManualOut,
        DepositIn,
        SettleOwnDeposit,
        SettleDepositOfAnotherSession,
    }

    [Property(MaxTest = 500)]
    public Property Expected_cash_follows_the_movements_of_the_day()
    {
        var day =
            from openingFloat in Gen.Choose(0, 300_000)
            from count in Gen.Choose(0, 40)
            from operations in Gen.ArrayOf(Operations(), count)
            select (openingFloat: openingFloat / 1000m, operations);

        return Prop.ForAll(day.ToArbitrary(), d =>
        {
            var session = Open(d.openingFloat);
            var revenue = Tnd(d.openingFloat);
            var held = new List<(Guid Id, Money Amount)>();
            foreach (var (kind, millimes, share) in d.operations)
            {
                var amount = Tnd(millimes / 1000m);
                var before = session.Movements.Count;
                switch (kind)
                {
                    case OperationKind.Payment:
                        session.RecordPaymentReceived(Guid.CreateVersion7(), Guid.CreateVersion7(), amount, Agent, Morning).IsSuccess.ShouldBeTrue();
                        revenue += amount;
                        break;
                    case OperationKind.Refund or OperationKind.ManualOut:
                        var outflow = kind == OperationKind.Refund
                            ? session.RecordRefundPaid(Guid.CreateVersion7(), Guid.CreateVersion7(), amount, Agent, Morning)
                            : session.AddManualMovement(CashFlowDirection.Out, amount, "Dépôt en banque", Agent, Morning);
                        if (amount <= revenue)
                        {
                            outflow.IsSuccess.ShouldBeTrue();
                            revenue -= amount;
                        }
                        else
                        {
                            outflow.Error.ShouldBe(CashErrors.InsufficientCash);
                            session.Movements.Count.ShouldBe(before);
                        }

                        break;
                    case OperationKind.ManualIn:
                        session.AddManualMovement(CashFlowDirection.In, amount, "Monnaie", Agent, Morning).IsSuccess.ShouldBeTrue();
                        revenue += amount;
                        break;
                    case OperationKind.DepositIn:
                        var depositId = Guid.CreateVersion7();
                        session.RecordDepositReceived(depositId, Guid.CreateVersion7(), amount, Agent, Morning).IsSuccess.ShouldBeTrue();
                        held.Add((depositId, amount));
                        break;
                    case OperationKind.SettleOwnDeposit when held.Count > 0:
                        var (id, depositAmount) = held[millimes % held.Count];
                        var retained = depositAmount * (share / 100m);
                        session.RecordDepositSettlement(new DepositCashSettlement(id, Guid.CreateVersion7(), depositAmount, retained, session.Id), Agent, Morning)
                            .IsSuccess.ShouldBeTrue();
                        held.Remove((id, depositAmount));
                        revenue += retained;
                        break;
                    case OperationKind.SettleDepositOfAnotherSession:
                        var foreignRetained = amount * (share / 100m);
                        session.RecordDepositSettlement(
                                new DepositCashSettlement(Guid.CreateVersion7(), Guid.CreateVersion7(), amount, foreignRetained, Guid.CreateVersion7()),
                                Agent,
                                Morning)
                            .IsSuccess.ShouldBeTrue();
                        revenue += foreignRetained;
                        break;
                }

                session.ExpectedRevenueCash.ShouldBe(revenue);
                session.ExpectedDepositCash.ShouldBe(held.Select(h => h.Amount).Sum());
                session.ExpectedRevenueCash.IsNegative.ShouldBeFalse();
                session.ExpectedDepositCash.IsNegative.ShouldBeFalse();
            }

            session.Movements.Select(m => m.Sequence).ShouldBe(Enumerable.Range(1, session.Movements.Count));
            session.ExpectedRevenueCash.ShouldBe(session.OpeningFloat + session.Movements.Select(m => m.RevenueEffect).Sum());
            session.ExpectedDepositCash.ShouldBe(session.Movements.Select(m => m.DepositEffect).Sum());
            session.Movements.ShouldAllBe(m => m.Amount.IsPositive);
        });
    }

    private static Gen<(OperationKind Kind, int Millimes, int Share)> Operations() =>
        from kind in Gen.Elements(Enum.GetValues<OperationKind>())
        from millimes in Gen.Choose(1, 500_000)
        from share in Gen.Choose(0, 100)
        select (kind, millimes, share);
}
