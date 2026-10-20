using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Paging;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Payments.Fakes;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using static CarRental.Application.Tests.Payments.Fakes.FinanceHarness;

namespace CarRental.Application.Tests.Cash;

/// <summary>Cash sessions: one per agent and day, own session for agents, every session, lock and adjustments for managers.</summary>
public sealed class CashSessionServiceTests
{
    private readonly FinanceHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_opens_a_session_for_today_with_a_float()
    {
        var agent = _harness.User(Roles.Agent, name: "Agent Démo");

        var id = (await _harness.Sessions(agent).OpenAsync(new OpenCashSessionRequest(_harness.AgencyId, 150m), cancellationToken: Ct)).Value;

        var session = (await _harness.Sessions(agent).GetCurrentAsync(cancellationToken: Ct)).Value!;
        session.Id.ShouldBe(id);
        session.CashierId.ShouldBe(agent.UserId!.Value);
        session.CashierName.ShouldBe("Agent Démo");
        session.AgencyId.ShouldBe(_harness.AgencyId);
        session.BusinessDate.ShouldBe(new DateOnly(2027, 6, 28));
        session.Status.ShouldBe(CashSessionStatusInfo.Open);
        session.OpeningFloat.ShouldBe(Tnd(150m));
        session.ExpectedRevenueCash.ShouldBe(Tnd(150m));
        session.ExpectedDepositCash.ShouldBe(Money.Zero);
        session.Movements.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_agent_has_one_session_a_day_and_one_open_at_a_time()
    {
        var agent = _harness.User(Roles.Agent);
        var sessions = _harness.Sessions(agent);
        var id = await _harness.OpenSessionAsync(agent, cancellationToken: Ct);

        (await sessions.OpenAsync(new OpenCashSessionRequest(_harness.AgencyId, 0m), cancellationToken: Ct)).Error.ShouldBe(CashErrors.SessionAlreadyOpen);
        (await sessions.CloseAsync(id, new CloseCashSessionRequest(100m, 0m), cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        (await sessions.OpenAsync(new OpenCashSessionRequest(_harness.AgencyId, 0m), cancellationToken: Ct)).Error.ShouldBe(CashErrors.SessionAlreadyExistsForDay);

        _harness.Time.Advance(TimeSpan.FromDays(1));
        (await sessions.OpenAsync(new OpenCashSessionRequest(_harness.AgencyId, 0m), cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        (await _harness.Sessions(_harness.User(Roles.Agent)).OpenAsync(new OpenCashSessionRequest(_harness.AgencyId, 0m), cancellationToken: Ct)).IsSuccess.ShouldBeTrue("another agent has a drawer of their own");
    }

    [Fact]
    public async Task Opening_is_validated()
    {
        var inactive = Guid.CreateVersion7();
        _harness.Referential.Agencies.Add(new AgencyInfo(inactive, "Ancienne agence", "Sousse", null, null, IsActive: false));
        var sessions = _harness.Sessions(_harness.User(Roles.Agent));

        (await sessions.OpenAsync(new OpenCashSessionRequest(Guid.CreateVersion7(), 0m), cancellationToken: Ct)).Error.ShouldBe(CashErrors.AgencyNotFound);
        (await sessions.OpenAsync(new OpenCashSessionRequest(inactive, 0m), cancellationToken: Ct)).Error.ShouldBe(CashErrors.AgencyNotFound);
        (await sessions.OpenAsync(new OpenCashSessionRequest(_harness.AgencyId, -1m), cancellationToken: Ct)).Error.ShouldBe(CashErrors.OpeningFloatInvalid);
        (await sessions.OpenAsync(new OpenCashSessionRequest(_harness.AgencyId, 10.0001m), cancellationToken: Ct)).Error.ShouldBe(CashErrors.OpeningFloatInvalid);
        (await _harness.Sessions(_harness.User(Roles.Preparer)).OpenAsync(new OpenCashSessionRequest(_harness.AgencyId, 0m), cancellationToken: Ct)).Error.ShouldBe(CashErrors.Forbidden);
        _harness.Db.Rows<CashSession>().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_current_session_lists_its_movements_with_running_balances()
    {
        var agent = _harness.User(Roles.Agent);
        await _harness.OpenSessionAsync(agent, openingFloat: 100m, cancellationToken: Ct);
        var sessions = _harness.Sessions(agent);
        var bookingId = Guid.CreateVersion7();
        await _harness.Payments(agent).RecordPaymentAsync(CashPayment(bookingId, _harness.AgencyId, 250m), cancellationToken: Ct);
        await _harness.Deposits(agent).TakeAsync(new DepositRequest(bookingId, _harness.AgencyId, DepositModeInfo.Cash, 400m), cancellationToken: Ct);
        var sessionId = (await sessions.GetCurrentAsync(cancellationToken: Ct)).Value!.Id;
        (await sessions.AddManualMovementAsync(sessionId, new ManualCashMovementRequest(CashFlowDirectionInfo.Out, 30m, "Dépôt en banque"), cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        var current = (await sessions.GetCurrentAsync(cancellationToken: Ct)).Value!;

        current.Movements.Select(m => (m.Sequence, m.Kind, m.RevenueBalance.Amount, m.DepositBalance.Amount)).ShouldBe(
        [
            (1, CashMovementKindInfo.PaymentReceived, 350m, 0m),
            (2, CashMovementKindInfo.DepositReceived, 350m, 400m),
            (3, CashMovementKindInfo.ManualOut, 320m, 400m),
        ]);
        current.Movements[2].Reason.ShouldBe("Dépôt en banque");
        current.Movements[0].BookingId.ShouldBe(bookingId);
        current.ExpectedRevenueCash.ShouldBe(Tnd(320m));
        current.ExpectedDepositCash.ShouldBe(Tnd(400m));
        current.Totals.PaymentsReceived.ShouldBe(Tnd(250m));
        current.Totals.DepositsReceived.ShouldBe(Tnd(400m));
        current.Totals.ManualOut.ShouldBe(Tnd(30m));
        current.Totals.RefundsPaid.ShouldBe(Money.Zero);
    }

    [Fact]
    public async Task Without_an_open_session_the_current_session_is_empty()
    {
        (await _harness.Sessions(_harness.User(Roles.Agent)).GetCurrentAsync(cancellationToken: Ct)).Value.ShouldBeNull();
        (await _harness.Sessions(_harness.User(Roles.Preparer)).GetCurrentAsync(cancellationToken: Ct)).Error.ShouldBe(CashErrors.Forbidden);
    }

    [Fact]
    public async Task An_agent_only_reaches_their_own_session()
    {
        var owner = _harness.User(Roles.Agent);
        var sessionId = await _harness.OpenSessionAsync(owner, cancellationToken: Ct);
        var other = _harness.Sessions(_harness.User(Roles.Agent));

        (await other.GetAsync(sessionId, cancellationToken: Ct)).Error.ShouldBe(CashErrors.SessionNotOwned);
        (await other.AddManualMovementAsync(sessionId, new ManualCashMovementRequest(CashFlowDirectionInfo.In, 10m, "Monnaie"), cancellationToken: Ct)).Error.ShouldBe(CashErrors.SessionNotOwned);
        (await other.CloseAsync(sessionId, new CloseCashSessionRequest(100m, 0m), cancellationToken: Ct)).Error.ShouldBe(CashErrors.SessionNotOwned);
        (await other.GetAsync(Guid.CreateVersion7(), cancellationToken: Ct)).Error.ShouldBe(CashErrors.SessionNotFound);
        _harness.Session(sessionId).Movements.ShouldBeEmpty();

        (await _harness.Sessions(_harness.User(Roles.Accountant)).GetAsync(sessionId, cancellationToken: Ct)).IsSuccess.ShouldBeTrue("the accountant sees every session");
        (await _harness.Sessions(_harness.User(Roles.Manager)).AddManualMovementAsync(sessionId, new ManualCashMovementRequest(CashFlowDirectionInfo.In, 10m, "Monnaie apportée par le gérant"), cancellationToken: Ct))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_manual_movement_needs_a_valid_amount_and_a_reason()
    {
        var agent = _harness.User(Roles.Agent);
        var sessionId = await _harness.OpenSessionAsync(agent, openingFloat: 20m, cancellationToken: Ct);
        var sessions = _harness.Sessions(agent);

        (await sessions.AddManualMovementAsync(sessionId, new ManualCashMovementRequest(CashFlowDirectionInfo.In, 0m, "Monnaie"), cancellationToken: Ct)).Error.ShouldBe(CashErrors.AmountInvalid);
        (await sessions.AddManualMovementAsync(sessionId, new ManualCashMovementRequest(CashFlowDirectionInfo.In, 1.0001m, "Monnaie"), cancellationToken: Ct)).Error.ShouldBe(CashErrors.AmountInvalid);
        (await sessions.AddManualMovementAsync(sessionId, new ManualCashMovementRequest(CashFlowDirectionInfo.In, 10m, "  "), cancellationToken: Ct)).Error.ShouldBe(CashErrors.ReasonRequired);
        (await sessions.AddManualMovementAsync(sessionId, new ManualCashMovementRequest(CashFlowDirectionInfo.Out, 25m, "Dépôt en banque"), cancellationToken: Ct)).Error.ShouldBe(CashErrors.InsufficientCash);
        (await sessions.AddManualMovementAsync(Guid.CreateVersion7(), new ManualCashMovementRequest(CashFlowDirectionInfo.In, 10m, "Monnaie"), cancellationToken: Ct)).Error.ShouldBe(CashErrors.SessionNotFound);
        _harness.Session(sessionId).Movements.ShouldBeEmpty();
    }

    [Fact]
    public async Task Closing_with_a_variance_needs_a_justification_and_returns_the_variances()
    {
        var agent = _harness.User(Roles.Agent);
        var sessionId = await _harness.OpenSessionAsync(agent, openingFloat: 100m, cancellationToken: Ct);
        await _harness.Payments(agent).RecordPaymentAsync(CashPayment(Guid.CreateVersion7(), _harness.AgencyId, 250m), cancellationToken: Ct);
        var sessions = _harness.Sessions(agent);

        (await sessions.CloseAsync(sessionId, new CloseCashSessionRequest(345m, 0m), cancellationToken: Ct)).Error.ShouldBe(CashErrors.VarianceJustificationRequired);
        (await sessions.CloseAsync(sessionId, new CloseCashSessionRequest(-1m, 0m), cancellationToken: Ct)).Error.ShouldBe(CashErrors.CountedAmountInvalid);
        (await sessions.CloseAsync(sessionId, new CloseCashSessionRequest(345m, 0.0001m), cancellationToken: Ct)).Error.ShouldBe(CashErrors.CountedAmountInvalid);
        _harness.Time.Advance(TimeSpan.FromHours(10));

        var closing = (await sessions.CloseAsync(sessionId, new CloseCashSessionRequest(345m, 0m, "Erreur de rendu de monnaie"), cancellationToken: Ct)).Value;

        closing.ShouldBe(new CashSessionClosing(Tnd(-5m), Money.Zero));
        var session = (await sessions.GetAsync(sessionId, cancellationToken: Ct)).Value;
        session.Status.ShouldBe(CashSessionStatusInfo.Closed);
        session.CountedRevenueCash.ShouldBe(Tnd(345m));
        session.VarianceJustification.ShouldBe("Erreur de rendu de monnaie");
        session.ClosedAtUtc.ShouldBe(Start.AddHours(10));
        (await sessions.GetCurrentAsync(cancellationToken: Ct)).Value.ShouldBeNull();
        (await _harness.Payments(agent).RecordPaymentAsync(CashPayment(Guid.CreateVersion7(), _harness.AgencyId, 10m), cancellationToken: Ct)).Error.ShouldBe(CashErrors.NoOpenSession);
    }

    [Fact]
    public async Task Only_a_manager_or_the_accountant_locks_a_closed_session()
    {
        var agent = _harness.User(Roles.Agent);
        var sessionId = await _harness.OpenSessionAsync(agent, cancellationToken: Ct);
        var accountant = _harness.Sessions(_harness.User(Roles.Accountant, name: "Comptable Démo"));

        (await accountant.LockAsync(sessionId, cancellationToken: Ct)).Error.ShouldBe(CashErrors.SessionNotClosed);
        (await _harness.Sessions(agent).CloseAsync(sessionId, new CloseCashSessionRequest(100m, 0m), cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        (await _harness.Sessions(agent).LockAsync(sessionId, cancellationToken: Ct)).Error.ShouldBe(CashErrors.Forbidden);

        (await accountant.LockAsync(sessionId, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        var locked = (await accountant.GetAsync(sessionId, cancellationToken: Ct)).Value;
        locked.Status.ShouldBe(CashSessionStatusInfo.Locked);
        locked.LockedByName.ShouldStartWith("accountant-");
        (await accountant.LockAsync(sessionId, cancellationToken: Ct)).Error.ShouldBe(CashErrors.SessionLocked);
        (await _harness.Sessions(_harness.User(Roles.Manager)).AddManualMovementAsync(sessionId, new ManualCashMovementRequest(CashFlowDirectionInfo.In, 1m, "Correction"), cancellationToken: Ct))
            .Error.ShouldBe(CashErrors.SessionLocked);
    }

    [Fact]
    public async Task A_locked_session_is_corrected_by_an_adjustment_in_a_later_session()
    {
        var agent = _harness.User(Roles.Agent);
        var monday = await _harness.OpenSessionAsync(agent, openingFloat: 100m, cancellationToken: Ct);
        await _harness.Sessions(agent).CloseAsync(monday, new CloseCashSessionRequest(95m, 0m, "Écart inexpliqué"), cancellationToken: Ct);
        var manager = _harness.Sessions(_harness.User(Roles.Manager));
        await manager.LockAsync(monday, cancellationToken: Ct);
        _harness.Time.Advance(TimeSpan.FromDays(1));
        var tuesday = await _harness.OpenSessionAsync(agent, openingFloat: 100m, cancellationToken: Ct);

        (await _harness.Sessions(agent).AddAdjustmentAsync(tuesday, new CashAdjustmentRequest(CashPocketInfo.Revenue, 5m, "Remboursement oublié lundi", monday), cancellationToken: Ct))
            .Error.ShouldBe(CashErrors.Forbidden);
        (await manager.AddAdjustmentAsync(tuesday, new CashAdjustmentRequest(CashPocketInfo.Revenue, 5m, "Lundi", tuesday), cancellationToken: Ct)).Error.ShouldBe(CashErrors.CorrectedSessionInvalid);
        (await manager.AddAdjustmentAsync(tuesday, new CashAdjustmentRequest(CashPocketInfo.Revenue, 5m, "Lundi", Guid.CreateVersion7()), cancellationToken: Ct)).Error.ShouldBe(CashErrors.CorrectedSessionInvalid);
        (await manager.AddAdjustmentAsync(tuesday, new CashAdjustmentRequest(CashPocketInfo.Revenue, 0m, "Lundi", monday), cancellationToken: Ct)).Error.ShouldBe(CashErrors.AmountInvalid);

        var movementId = (await manager.AddAdjustmentAsync(tuesday, new CashAdjustmentRequest(CashPocketInfo.Revenue, -5m, "Remboursement espèces oublié lundi", monday), cancellationToken: Ct)).Value;

        var adjustment = _harness.Session(tuesday).Movements.ShouldHaveSingleItem();
        adjustment.Id.ShouldBe(movementId);
        adjustment.Kind.ShouldBe(CashMovementKind.Adjustment);
        adjustment.RelatedSessionId.ShouldBe(monday);
        adjustment.RevenueEffect.ShouldBe(Tnd(-5m));
        _harness.Session(tuesday).ExpectedRevenueCash.ShouldBe(Tnd(95m));
        _harness.Session(monday).Movements.ShouldBeEmpty("a locked session never changes");
    }

    [Fact]
    public async Task Managers_list_every_session_with_totals_and_agents_only_their_own()
    {
        var first = _harness.User(Roles.Agent, name: "Amira");
        var second = _harness.User(Roles.Agent, name: "Bilel");
        var firstSession = await _harness.OpenSessionAsync(first, openingFloat: 100m, cancellationToken: Ct);
        await _harness.OpenSessionAsync(second, openingFloat: 50m, cancellationToken: Ct);
        await _harness.Sessions(first).CloseAsync(firstSession, new CloseCashSessionRequest(98m, 0m, "Pièce perdue"), cancellationToken: Ct);
        var manager = _harness.Sessions(_harness.User(Roles.Manager));
        var page = new PageRequest(1, 25);

        var all = (await manager.ListAsync(new CashSessionFilter(), page, cancellationToken: Ct)).Value;

        all.Page.Items.Select(s => s.CashierName).ShouldBe(["Amira", "Bilel"]);
        all.Totals.ShouldBe(new CashSessionListTotals(2, Tnd(150m), Tnd(150m), Money.Zero, Tnd(98m), Money.Zero, Tnd(-2m), Money.Zero));
        (await manager.ListAsync(new CashSessionFilter(Status: CashSessionStatusInfo.Open), page, cancellationToken: Ct)).Value.Page.Items.ShouldHaveSingleItem().CashierName.ShouldBe("Bilel");
        (await manager.ListAsync(new CashSessionFilter(CashierId: first.UserId), page, cancellationToken: Ct)).Value.Page.Items.ShouldHaveSingleItem().Id.ShouldBe(firstSession);
        (await manager.ListAsync(new CashSessionFilter(From: new DateOnly(2027, 6, 29)), page, cancellationToken: Ct)).Value.Page.TotalCount.ShouldBe(0);

        var own = (await _harness.Sessions(second).ListAsync(new CashSessionFilter(CashierId: first.UserId), page, cancellationToken: Ct)).Value;
        own.Page.Items.ShouldHaveSingleItem().CashierName.ShouldBe("Bilel");
        (await _harness.Sessions(_harness.User(Roles.Preparer)).ListAsync(new CashSessionFilter(), page, cancellationToken: Ct)).Error.ShouldBe(CashErrors.Forbidden);

        (await manager.ListCashiersAsync(cancellationToken: Ct)).Value.Select(c => c.Name).ShouldBe(["Amira", "Bilel"]);
        (await _harness.Sessions(second).ListCashiersAsync(cancellationToken: Ct)).Value.ShouldHaveSingleItem().Name.ShouldBe("Bilel");
    }

    [Fact]
    public async Task The_cash_drawer_only_records_inside_a_unit_of_work()
    {
        var agent = _harness.User(Roles.Agent);
        var sessionId = await _harness.OpenSessionAsync(agent, cancellationToken: Ct);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _harness.Drawer(agent).RecordPaymentReceivedAsync(new CashPaymentEntry(sessionId, Guid.CreateVersion7(), Guid.CreateVersion7(), Tnd(10m)), cancellationToken: Ct));
    }

    [Fact]
    public async Task The_reconciliation_query_totals_each_session_by_kind_of_movement()
    {
        var agent = _harness.User(Roles.Agent);
        await _harness.OpenSessionAsync(agent, openingFloat: 100m, cancellationToken: Ct);
        var paymentId = (await _harness.Payments(agent).RecordPaymentAsync(CashPayment(Guid.CreateVersion7(), _harness.AgencyId, 300m), cancellationToken: Ct)).Value;
        await _harness.Payments(agent).RecordRefundAsync(new CounterRefundRequest(paymentId, PaymentMethodInfo.Cash, 40m, "Option retirée"), cancellationToken: Ct);

        var totals = (await _harness.CashQueries().GetSessionTotalsAsync(new DateOnly(2027, 6, 28), new DateOnly(2027, 6, 28), cancellationToken: Ct)).ShouldHaveSingleItem();

        totals.OpeningFloat.ShouldBe(Tnd(100m));
        totals.Totals.PaymentsReceived.ShouldBe(Tnd(300m));
        totals.Totals.RefundsPaid.ShouldBe(Tnd(40m));
        totals.ExpectedRevenueCash.ShouldBe(Tnd(360m));
        (await _harness.CashQueries().GetSessionTotalsAsync(new DateOnly(2027, 6, 29), new DateOnly(2027, 6, 30), cancellationToken: Ct)).ShouldBeEmpty();
        (await _harness.CashQueries().GetSessionTotalsAsync(new DateOnly(2027, 6, 28), new DateOnly(2027, 6, 28), Guid.CreateVersion7(), cancellationToken: Ct)).ShouldBeEmpty();
    }
}
