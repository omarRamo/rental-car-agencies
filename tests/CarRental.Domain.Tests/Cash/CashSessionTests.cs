using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Cash.CashTestData;

namespace CarRental.Domain.Tests.Cash;

/// <summary>Cash sessions: opening float, typed movements, revenue and deposit cash, closing count, lock.</summary>
public sealed class CashSessionTests
{
    [Fact]
    public void Opening_sets_the_float_and_the_tunis_business_date()
    {
        // 23:30 UTC on the 27th is 00:30 on the 28th in Tunis.
        var openedAt = new DateTimeOffset(2027, 6, 27, 23, 30, 0, TimeSpan.Zero);

        var session = CashSession.Open(AgencyId, Agent.UserId!.Value, "  Agent Démo ", Tnd(150m), openedAt).Value;

        session.BusinessDate.ShouldBe(new DateOnly(2027, 6, 28));
        session.Status.ShouldBe(CashSessionStatus.Open);
        session.IsOpen.ShouldBeTrue();
        session.AgencyId.ShouldBe(AgencyId);
        session.CashierId.ShouldBe(Agent.UserId!.Value);
        session.CashierName.ShouldBe("Agent Démo");
        session.OpeningFloat.ShouldBe(Tnd(150m));
        session.ExpectedRevenueCash.ShouldBe(Tnd(150m));
        session.ExpectedDepositCash.ShouldBe(Money.Zero);
        session.OpenedAtUtc.ShouldBe(openedAt);
        session.Movements.ShouldBeEmpty();
        session.DomainEvents.ShouldHaveSingleItem().ShouldBe(new CashSessionOpened(session.Id, AgencyId, Agent.UserId!.Value));
    }

    [Fact]
    public void Opening_validates_its_input()
    {
        var cashier = Agent.UserId!.Value;

        CashSession.Open(Guid.Empty, cashier, "Agent", Tnd(10m), Morning).Error.ShouldBe(CashErrors.AgencyRequired);
        CashSession.Open(AgencyId, Guid.Empty, "Agent", Tnd(10m), Morning).Error.ShouldBe(CashErrors.CashierRequired);
        CashSession.Open(AgencyId, cashier, "Agent", Tnd(-0.001m), Morning).Error.ShouldBe(CashErrors.OpeningFloatInvalid);
        CashSession.Open(AgencyId, cashier, "Agent", Money.Of(10m, Currency.Eur), Morning).Error.ShouldBe(CashErrors.OpeningFloatInvalid);
        CashSession.Open(AgencyId, cashier, "Agent", Tnd(1_000_000.001m), Morning).Error.ShouldBe(CashErrors.OpeningFloatInvalid);
        CashSession.Open(AgencyId, cashier, "Agent", Money.Zero, Morning).IsSuccess.ShouldBeTrue("an empty drawer is allowed");
        CashSession.Open(AgencyId, cashier, " ", Money.Zero, Morning).Value.CashierName.ShouldBe(cashier.ToString());
    }

    [Fact]
    public void Payments_refunds_and_manual_movements_change_the_revenue_cash_only()
    {
        var session = Open(100m);
        var booking = Guid.CreateVersion7();
        var payment = Guid.CreateVersion7();
        var refund = Guid.CreateVersion7();

        session.RecordPaymentReceived(payment, booking, Tnd(250m), Agent, Morning.AddHours(1)).IsSuccess.ShouldBeTrue();
        session.RecordRefundPaid(refund, booking, Tnd(50m), Agent, Morning.AddHours(2)).IsSuccess.ShouldBeTrue();
        session.AddManualMovement(CashFlowDirection.In, Tnd(20m), "Monnaie de la banque", Agent, Morning.AddHours(3)).IsSuccess.ShouldBeTrue();
        var manualOut = session.AddManualMovement(CashFlowDirection.Out, Tnd(70.5m), "  Achat de fournitures ", Agent, Morning.AddHours(4)).Value;

        session.ExpectedRevenueCash.ShouldBe(Tnd(249.5m));
        session.ExpectedDepositCash.ShouldBe(Money.Zero);
        session.Movements.Select(m => (m.Sequence, m.Kind, m.RevenueEffect.Amount, m.DepositEffect.Amount)).ShouldBe(
        [
            (1, CashMovementKind.PaymentReceived, 250m, 0m),
            (2, CashMovementKind.RefundPaid, -50m, 0m),
            (3, CashMovementKind.ManualIn, 20m, 0m),
            (4, CashMovementKind.ManualOut, -70.5m, 0m),
        ]);
        session.LastSequence.ShouldBe(4);
        session.Movements[0].PaymentId.ShouldBe(payment);
        session.Movements[0].BookingId.ShouldBe(booking);
        session.Movements[1].PaymentId.ShouldBe(refund);
        manualOut.Amount.ShouldBe(Tnd(70.5m));
        manualOut.Reason.ShouldBe("Achat de fournitures");
        manualOut.SessionId.ShouldBe(session.Id);
        manualOut.RecordedAtUtc.ShouldBe(Morning.AddHours(4));
        manualOut.RecordedByUserId.ShouldBe(Agent.UserId);
        manualOut.RecordedByName.ShouldBe("agent@demo.local");
        session.DomainEvents.OfType<CashMovementRecorded>().Select(e => e.Kind).ShouldBe(
            [CashMovementKind.PaymentReceived, CashMovementKind.RefundPaid, CashMovementKind.ManualIn, CashMovementKind.ManualOut]);
    }

    [Fact]
    public void Cash_deposits_are_counted_apart_from_the_revenue_cash()
    {
        var session = Open(100m);
        var deposit = Guid.CreateVersion7();

        session.RecordDepositReceived(deposit, Guid.CreateVersion7(), Tnd(500m), Agent, Morning).IsSuccess.ShouldBeTrue();

        session.ExpectedDepositCash.ShouldBe(Tnd(500m));
        session.ExpectedRevenueCash.ShouldBe(Tnd(100m), "a deposit is owed to the customer, never revenue");

        session.RecordDepositSettlement(Settlement(deposit, 500m, 0m, session.Id), Agent, Morning.AddHours(8)).IsSuccess.ShouldBeTrue();

        session.ExpectedDepositCash.ShouldBe(Money.Zero);
        session.ExpectedRevenueCash.ShouldBe(Tnd(100m));
        session.Movements.Select(m => m.Kind).ShouldBe([CashMovementKind.DepositReceived, CashMovementKind.DepositReturned]);
    }

    [Fact]
    public void The_retained_part_of_a_cash_deposit_becomes_revenue_cash()
    {
        var session = Open(100m);
        var deposit = Guid.CreateVersion7();
        session.RecordDepositReceived(deposit, Guid.CreateVersion7(), Tnd(500m), Agent, Morning);

        var movements = session.RecordDepositSettlement(Settlement(deposit, 500m, 120m, session.Id), Agent, Morning).Value;

        movements.Select(m => (m.Kind, m.Amount.Amount, m.RevenueEffect.Amount, m.DepositEffect.Amount)).ShouldBe(
        [
            (CashMovementKind.DepositReturned, 380m, 0m, -380m),
            (CashMovementKind.DepositRetained, 120m, 120m, -120m),
        ]);
        session.ExpectedDepositCash.ShouldBe(Money.Zero);
        session.ExpectedRevenueCash.ShouldBe(Tnd(220m));
    }

    [Fact]
    public void A_deposit_retained_in_full_returns_nothing()
    {
        var session = Open(0m);
        var deposit = Guid.CreateVersion7();
        session.RecordDepositReceived(deposit, Guid.CreateVersion7(), Tnd(300m), Agent, Morning);

        session.RecordDepositSettlement(Settlement(deposit, 300m, 300m, session.Id), Agent, Morning).Value
            .ShouldHaveSingleItem().Kind.ShouldBe(CashMovementKind.DepositRetained);

        session.ExpectedRevenueCash.ShouldBe(Tnd(300m));
        session.ExpectedDepositCash.ShouldBe(Money.Zero);
    }

    [Fact]
    public void A_cash_deposit_received_in_another_session_is_brought_into_the_drawer_before_it_is_settled()
    {
        var departure = Open(0m);
        var deposit = Guid.CreateVersion7();
        departure.RecordDepositReceived(deposit, Guid.CreateVersion7(), Tnd(500m), Agent, Morning);
        var returnDay = Open(50m, Morning.AddDays(5));

        var movements = returnDay.RecordDepositSettlement(Settlement(deposit, 500m, 80m, departure.Id), Agent, Morning.AddDays(5)).Value;

        movements.Select(m => (m.Kind, m.DepositEffect.Amount)).ShouldBe(
        [
            (CashMovementKind.DepositTransferIn, 500m),
            (CashMovementKind.DepositReturned, -420m),
            (CashMovementKind.DepositRetained, -80m),
        ]);
        movements[0].RelatedSessionId.ShouldBe(departure.Id);
        returnDay.ExpectedDepositCash.ShouldBe(Money.Zero);
        returnDay.ExpectedRevenueCash.ShouldBe(Tnd(130m));
    }

    [Fact]
    public void A_deposit_handed_over_leaves_the_open_drawer_that_received_it()
    {
        var morning = Open(0m);
        var deposit = Guid.CreateVersion7();
        morning.RecordDepositReceived(deposit, Guid.CreateVersion7(), Tnd(500m), Agent, Morning);
        var otherSession = Guid.CreateVersion7();

        var handover = morning.RecordDepositHandedOver(deposit, Guid.CreateVersion7(), Tnd(500m), otherSession, Manager, Morning.AddHours(6)).Value;

        handover.Kind.ShouldBe(CashMovementKind.DepositTransferOut);
        handover.RelatedSessionId.ShouldBe(otherSession);
        handover.RecordedByUserId.ShouldBe(Manager.UserId);
        morning.ExpectedDepositCash.ShouldBe(Money.Zero);
        morning.RecordDepositHandedOver(deposit, Guid.CreateVersion7(), Tnd(500m), otherSession, Manager, Morning).Error.ShouldBe(CashErrors.DuplicateMovement);
    }

    [Fact]
    public void An_outflow_never_exceeds_the_expected_cash_of_its_pocket()
    {
        var session = Open(50m);
        var deposit = Guid.CreateVersion7();

        session.RecordRefundPaid(Guid.CreateVersion7(), Guid.CreateVersion7(), Tnd(50.001m), Agent, Morning).Error.ShouldBe(CashErrors.InsufficientCash);
        session.AddManualMovement(CashFlowDirection.Out, Tnd(60m), "Dépôt en banque", Agent, Morning).Error.ShouldBe(CashErrors.InsufficientCash);
        session.AddAdjustment(CashPocket.Revenue, Tnd(-50.001m), "Correction", null, Manager, Morning).Error.ShouldBe(CashErrors.InsufficientCash);
        session.AddAdjustment(CashPocket.Deposit, Tnd(-1m), "Correction", null, Manager, Morning).Error.ShouldBe(CashErrors.InsufficientCash);
        session.RecordDepositSettlement(Settlement(deposit, 200m, 0m, session.Id), Agent, Morning).Error.ShouldBe(CashErrors.InsufficientCash, "the deposit is not in this drawer");

        session.Movements.ShouldBeEmpty();
        session.LastSequence.ShouldBe(0);
        session.ExpectedRevenueCash.ShouldBe(Tnd(50m));
        session.RecordRefundPaid(Guid.CreateVersion7(), Guid.CreateVersion7(), Tnd(50m), Agent, Morning).IsSuccess.ShouldBeTrue("the drawer can be emptied");
        session.ExpectedRevenueCash.ShouldBe(Money.Zero);
    }

    [Fact]
    public void A_manual_movement_needs_a_reason()
    {
        var session = Open();

        session.AddManualMovement(CashFlowDirection.In, Tnd(10m), null, Agent, Morning).Error.ShouldBe(CashErrors.ReasonRequired);
        session.AddManualMovement(CashFlowDirection.In, Tnd(10m), " ab ", Agent, Morning).Error.ShouldBe(CashErrors.ReasonRequired);
        session.AddManualMovement(CashFlowDirection.In, Tnd(10m), new string('x', CashMovement.ReasonMaxLength + 1), Agent, Morning).Error.ShouldBe(CashErrors.ReasonTooLong);
        session.AddManualMovement((CashFlowDirection)5, Tnd(10m), "Monnaie", Agent, Morning).Error.ShouldBe(CashErrors.DirectionInvalid);
        session.Movements.ShouldBeEmpty();
    }

    [Fact]
    public void Amounts_are_positive_millimes_in_dinars()
    {
        var session = Open();

        session.RecordPaymentReceived(Guid.CreateVersion7(), Guid.CreateVersion7(), Money.Zero, Agent, Morning).Error.ShouldBe(CashErrors.AmountInvalid);
        session.RecordPaymentReceived(Guid.CreateVersion7(), Guid.CreateVersion7(), Tnd(-1m), Agent, Morning).Error.ShouldBe(CashErrors.AmountInvalid);
        session.RecordPaymentReceived(Guid.CreateVersion7(), Guid.CreateVersion7(), Money.Of(10m, Currency.Eur), Agent, Morning).Error.ShouldBe(CashErrors.AmountInvalid);
        session.RecordPaymentReceived(Guid.CreateVersion7(), Guid.CreateVersion7(), Tnd(1_000_000.001m), Agent, Morning).Error.ShouldBe(CashErrors.AmountInvalid);
        session.AddManualMovement(CashFlowDirection.In, Tnd(-5m), "Monnaie", Agent, Morning).Error.ShouldBe(CashErrors.AmountInvalid);
        session.RecordDepositSettlement(Settlement(Guid.CreateVersion7(), 100m, 100.001m, session.Id), Agent, Morning).Error.ShouldBe(CashErrors.AmountInvalid);
        session.RecordDepositSettlement(Settlement(Guid.CreateVersion7(), 100m, -1m, session.Id), Agent, Morning).Error.ShouldBe(CashErrors.AmountInvalid);
        session.RecordDepositSettlement(Settlement(Guid.CreateVersion7(), 0m, 0m, session.Id), Agent, Morning).Error.ShouldBe(CashErrors.AmountInvalid);
        session.Movements.ShouldBeEmpty();
    }

    [Fact]
    public void A_payment_or_a_deposit_is_recorded_once_in_a_session()
    {
        var session = Open(100m);
        var payment = Guid.CreateVersion7();
        var deposit = Guid.CreateVersion7();
        session.RecordPaymentReceived(payment, Guid.CreateVersion7(), Tnd(10m), Agent, Morning);
        session.RecordDepositReceived(deposit, Guid.CreateVersion7(), Tnd(300m), Agent, Morning);
        session.RecordDepositSettlement(Settlement(deposit, 300m, 0m, session.Id), Agent, Morning);

        session.RecordPaymentReceived(payment, Guid.CreateVersion7(), Tnd(10m), Agent, Morning).Error.ShouldBe(CashErrors.DuplicateMovement);
        session.RecordDepositReceived(deposit, Guid.CreateVersion7(), Tnd(300m), Agent, Morning).Error.ShouldBe(CashErrors.DuplicateMovement);
        session.RecordDepositSettlement(Settlement(deposit, 300m, 0m, Guid.CreateVersion7()), Agent, Morning).Error.ShouldBe(CashErrors.DuplicateMovement);
        session.Movements.Count.ShouldBe(3, "a rejected settlement appends none of its movements");
        session.RecordRefundPaid(payment, Guid.CreateVersion7(), Tnd(10m), Agent, Morning).IsSuccess.ShouldBeTrue("another kind of movement");
    }

    [Fact]
    public void An_adjustment_corrects_one_pocket_by_a_signed_amount()
    {
        var session = Open(100m);
        var corrected = Guid.CreateVersion7();
        session.RecordDepositReceived(Guid.CreateVersion7(), Guid.CreateVersion7(), Tnd(10m), Agent, Morning);

        var revenue = session.AddAdjustment(CashPocket.Revenue, Tnd(5m), "Erreur de comptage du 27/06", corrected, Manager, Morning).Value;
        var deposit = session.AddAdjustment(CashPocket.Deposit, Tnd(-2m), "Caution comptée deux fois", null, Manager, Morning).Value;

        revenue.Kind.ShouldBe(CashMovementKind.Adjustment);
        revenue.Amount.ShouldBe(Tnd(5m));
        revenue.RevenueEffect.ShouldBe(Tnd(5m));
        revenue.DepositEffect.ShouldBe(Money.Zero);
        revenue.RelatedSessionId.ShouldBe(corrected);
        revenue.Reason.ShouldBe("Erreur de comptage du 27/06");
        deposit.Amount.ShouldBe(Tnd(2m));
        deposit.DepositEffect.ShouldBe(Tnd(-2m));
        session.ExpectedRevenueCash.ShouldBe(Tnd(105m));
        session.ExpectedDepositCash.ShouldBe(Tnd(8m));

        session.AddAdjustment(CashPocket.Revenue, Money.Zero, "Rien", null, Manager, Morning).Error.ShouldBe(CashErrors.AmountInvalid);
        session.AddAdjustment(CashPocket.Revenue, Tnd(1m), "Soi-même", session.Id, Manager, Morning).Error.ShouldBe(CashErrors.CorrectedSessionInvalid);
        session.AddAdjustment(CashPocket.Revenue, Tnd(1m), "Vide", Guid.Empty, Manager, Morning).Error.ShouldBe(CashErrors.CorrectedSessionInvalid);
        session.AddAdjustment((CashPocket)7, Tnd(1m), "Poche", null, Manager, Morning).Error.ShouldBe(CashErrors.PocketInvalid);
        session.AddAdjustment(CashPocket.Revenue, Tnd(1m), "", null, Manager, Morning).Error.ShouldBe(CashErrors.ReasonRequired);
    }

    [Fact]
    public void Closing_with_the_expected_cash_needs_no_justification()
    {
        var session = Open(100m);
        session.RecordPaymentReceived(Guid.CreateVersion7(), Guid.CreateVersion7(), Tnd(250m), Agent, Morning);
        session.RecordDepositReceived(Guid.CreateVersion7(), Guid.CreateVersion7(), Tnd(500m), Agent, Morning);
        session.DequeueDomainEvents();
        var closedAt = Morning.AddHours(10);

        session.Close(Tnd(350m), Tnd(500m), null, Agent, closedAt).IsSuccess.ShouldBeTrue();

        session.Status.ShouldBe(CashSessionStatus.Closed);
        session.CountedRevenueCash.ShouldBe(Tnd(350m));
        session.CountedDepositCash.ShouldBe(Tnd(500m));
        session.RevenueVariance.ShouldBe(Money.Zero);
        session.DepositVariance.ShouldBe(Money.Zero);
        session.HasVariance.ShouldBeFalse();
        session.VarianceJustification.ShouldBeNull();
        session.ClosedAtUtc.ShouldBe(closedAt);
        session.ClosedByUserId.ShouldBe(Agent.UserId);
        session.ClosedByName.ShouldBe("agent@demo.local");
        session.DomainEvents.ShouldHaveSingleItem().ShouldBe(new CashSessionClosed(session.Id, AgencyId, Agent.UserId!.Value, HasVariance: false));
    }

    [Fact]
    public void A_variance_on_either_pocket_needs_a_justification()
    {
        var session = Open(100m);
        session.RecordPaymentReceived(Guid.CreateVersion7(), Guid.CreateVersion7(), Tnd(250m), Agent, Morning);
        session.RecordDepositReceived(Guid.CreateVersion7(), Guid.CreateVersion7(), Tnd(500m), Agent, Morning);

        session.Close(Tnd(345m), Tnd(500m), null, Agent, Morning).Error.ShouldBe(CashErrors.VarianceJustificationRequired);
        session.Close(Tnd(350m), Tnd(450m), " ok ", Agent, Morning).Error.ShouldBe(CashErrors.VarianceJustificationRequired);
        session.Close(Tnd(345m), Tnd(500m), new string('x', CashSession.JustificationMaxLength + 1), Agent, Morning).Error.ShouldBe(CashErrors.JustificationTooLong);
        session.Status.ShouldBe(CashSessionStatus.Open);

        session.Close(Tnd(345m), Tnd(500.5m), "  Erreur de rendu de monnaie ", Agent, Morning).IsSuccess.ShouldBeTrue();

        session.RevenueVariance.ShouldBe(Tnd(-5m));
        session.DepositVariance.ShouldBe(Tnd(0.5m));
        session.HasVariance.ShouldBeTrue();
        session.VarianceJustification.ShouldBe("Erreur de rendu de monnaie");
        session.DomainEvents.OfType<CashSessionClosed>().ShouldHaveSingleItem().HasVariance.ShouldBeTrue();
    }

    [Fact]
    public void Counted_amounts_are_zero_or_positive_millimes()
    {
        var session = Open(0m);

        session.Close(Tnd(-1m), Money.Zero, "Erreur", Agent, Morning).Error.ShouldBe(CashErrors.CountedAmountInvalid);
        session.Close(Money.Zero, Money.Of(1m, Currency.Eur), "Erreur", Agent, Morning).Error.ShouldBe(CashErrors.CountedAmountInvalid);
        session.Close(Money.Zero, Money.Zero, null, Agent, Morning).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_closed_session_records_nothing_more_and_can_only_be_locked()
    {
        var session = Open(100m);
        var deposit = Guid.CreateVersion7();
        session.RecordDepositReceived(deposit, Guid.CreateVersion7(), Tnd(10m), Agent, Morning);
        session.Close(Tnd(100m), Tnd(10m), null, Agent, Morning).IsSuccess.ShouldBeTrue();

        session.RecordPaymentReceived(Guid.CreateVersion7(), Guid.CreateVersion7(), Tnd(10m), Agent, Morning).Error.ShouldBe(CashErrors.SessionClosed);
        session.AddManualMovement(CashFlowDirection.In, Tnd(10m), "Monnaie", Agent, Morning).Error.ShouldBe(CashErrors.SessionClosed);
        session.RecordDepositHandedOver(deposit, Guid.CreateVersion7(), Tnd(10m), Guid.CreateVersion7(), Agent, Morning).Error.ShouldBe(CashErrors.SessionClosed);
        session.Close(Tnd(100m), Tnd(10m), null, Agent, Morning).Error.ShouldBe(CashErrors.SessionClosed);
        session.DequeueDomainEvents();

        session.Lock(Manager, Morning.AddHours(12)).IsSuccess.ShouldBeTrue();

        session.Status.ShouldBe(CashSessionStatus.Locked);
        session.LockedAtUtc.ShouldBe(Morning.AddHours(12));
        session.LockedByUserId.ShouldBe(Manager.UserId);
        session.LockedByName.ShouldBe("gerant@demo.local");
        session.DomainEvents.ShouldHaveSingleItem().ShouldBe(new CashSessionLocked(session.Id, AgencyId, Agent.UserId!.Value));
    }

    [Fact]
    public void A_locked_session_never_changes()
    {
        var session = Open(100m);
        session.Close(Tnd(100m), Money.Zero, null, Agent, Morning);
        session.Lock(Manager, Morning);
        var deposit = Guid.CreateVersion7();

        session.RecordPaymentReceived(Guid.CreateVersion7(), Guid.CreateVersion7(), Tnd(1m), Agent, Morning).Error.ShouldBe(CashErrors.SessionLocked);
        session.RecordRefundPaid(Guid.CreateVersion7(), Guid.CreateVersion7(), Tnd(1m), Agent, Morning).Error.ShouldBe(CashErrors.SessionLocked);
        session.RecordDepositReceived(deposit, Guid.CreateVersion7(), Tnd(1m), Agent, Morning).Error.ShouldBe(CashErrors.SessionLocked);
        session.RecordDepositSettlement(Settlement(deposit, 1m, 0m, Guid.CreateVersion7()), Agent, Morning).Error.ShouldBe(CashErrors.SessionLocked);
        session.RecordDepositHandedOver(deposit, Guid.CreateVersion7(), Tnd(1m), Guid.CreateVersion7(), Agent, Morning).Error.ShouldBe(CashErrors.SessionLocked);
        session.AddManualMovement(CashFlowDirection.In, Tnd(1m), "Monnaie", Agent, Morning).Error.ShouldBe(CashErrors.SessionLocked);
        session.AddAdjustment(CashPocket.Revenue, Tnd(1m), "Correction", null, Manager, Morning).Error.ShouldBe(CashErrors.SessionLocked);
        session.Close(Tnd(100m), Money.Zero, null, Agent, Morning).Error.ShouldBe(CashErrors.SessionLocked);
        session.Lock(Manager, Morning).Error.ShouldBe(CashErrors.SessionLocked);

        session.Movements.ShouldBeEmpty();
        session.CountedRevenueCash.ShouldBe(Tnd(100m));
    }

    [Fact]
    public void Only_a_closed_session_can_be_locked()
    {
        Open().Lock(Manager, Morning).Error.ShouldBe(CashErrors.SessionNotClosed);
    }

    [Fact]
    public void A_session_stays_open_after_midnight_with_the_business_date_of_its_opening()
    {
        var session = Open(100m, new DateTimeOffset(2027, 6, 28, 15, 0, 0, TimeSpan.Zero));
        var afterMidnight = new DateTimeOffset(2027, 6, 28, 23, 45, 0, TimeSpan.Zero);

        session.RecordPaymentReceived(Guid.CreateVersion7(), Guid.CreateVersion7(), Tnd(80m), Agent, afterMidnight).IsSuccess.ShouldBeTrue();

        session.BusinessDate.ShouldBe(new DateOnly(2027, 6, 28));
        TunisTime.DateOf(afterMidnight).ShouldBe(new DateOnly(2027, 6, 29));
        session.ExpectedRevenueCash.ShouldBe(Tnd(180m));
    }
}
