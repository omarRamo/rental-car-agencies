using CarRental.Application.Cash.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Tests.Payments.Fakes;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using static CarRental.Application.Tests.Payments.Fakes.FinanceHarness;

namespace CarRental.Application.Tests.Payments;

/// <summary>Security deposits: taken, released or retained once; cash deposits through the deposit cash of the sessions.</summary>
public sealed class DepositsTests
{
    private readonly FinanceHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DepositRequest CardImprint(Guid bookingId, decimal amount = 800m) =>
        new(bookingId, _harness.AgencyId, DepositModeInfo.CardImprint, amount, "AUT-445566");

    private DepositRequest CashDeposit(Guid bookingId, decimal amount = 500m) =>
        new(bookingId, _harness.AgencyId, DepositModeInfo.Cash, amount);

    [Fact]
    public async Task A_card_imprint_is_held_until_the_return_and_owed_back_meanwhile()
    {
        var deposits = _harness.Deposits(_harness.User(Roles.Agent));
        var bookingId = Guid.CreateVersion7();

        var id = (await deposits.TakeAsync(CardImprint(bookingId), cancellationToken: Ct)).Value;

        var list = (await deposits.GetBookingDepositsAsync(bookingId, cancellationToken: Ct)).Value;
        var deposit = list.Deposits.ShouldHaveSingleItem();
        deposit.Id.ShouldBe(id);
        deposit.Mode.ShouldBe(DepositModeInfo.CardImprint);
        deposit.Status.ShouldBe(DepositStatusInfo.Held);
        deposit.ExternalReference.ShouldBe("AUT-445566");
        deposit.OpenLiability.ShouldBe(Tnd(800m));
        list.Totals.ShouldBe(new DepositTotals(Tnd(800m), Tnd(800m), Money.Zero, Money.Zero, 1));

        (await deposits.ReleaseAsync(id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        (await deposits.GetBookingDepositsAsync(bookingId, cancellationToken: Ct)).Value.Totals.ShouldBe(new DepositTotals(Tnd(800m), Money.Zero, Money.Zero, Tnd(800m), 0));
        (await deposits.ReleaseAsync(id, cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.DepositAlreadySettled);
        (await deposits.RetainAsync(id, new DepositRetentionRequest(10m, "Carburant"), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.DepositAlreadySettled);
    }

    [Fact]
    public async Task A_cash_deposit_is_counted_apart_in_the_session_of_the_agent()
    {
        var agent = _harness.User(Roles.Agent);
        var sessionId = await _harness.OpenSessionAsync(agent, openingFloat: 100m, cancellationToken: Ct);

        var id = (await _harness.Deposits(agent).TakeAsync(CashDeposit(Guid.CreateVersion7()), cancellationToken: Ct)).Value;

        _harness.Deposit(id).TakenInCashSessionId.ShouldBe(sessionId);
        var session = _harness.Session(sessionId);
        session.ExpectedDepositCash.ShouldBe(Tnd(500m));
        session.ExpectedRevenueCash.ShouldBe(Tnd(100m), "a deposit is never revenue");
        session.Movements.ShouldHaveSingleItem().Kind.ShouldBe(CashMovementKind.DepositReceived);
    }

    [Fact]
    public async Task A_cash_deposit_needs_an_open_session()
    {
        (await _harness.Deposits(_harness.User(Roles.Agent)).TakeAsync(CashDeposit(Guid.CreateVersion7()), cancellationToken: Ct)).Error.ShouldBe(CashErrors.NoOpenSession);
        _harness.Db.Rows<DepositHold>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_partial_retention_of_a_cash_deposit_returns_the_rest_and_keeps_the_retained_part_as_revenue_cash()
    {
        var agent = _harness.User(Roles.Agent);
        var sessionId = await _harness.OpenSessionAsync(agent, openingFloat: 100m, cancellationToken: Ct);
        var deposits = _harness.Deposits(agent);
        var id = (await deposits.TakeAsync(CashDeposit(Guid.CreateVersion7(), 500m), cancellationToken: Ct)).Value;

        (await deposits.RetainAsync(id, new DepositRetentionRequest(120m, "Jante rayée", "photo-retour-07.jpg"), cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        var deposit = _harness.Deposit(id);
        deposit.Status.ShouldBe(DepositStatus.PartiallyRetained);
        deposit.RetainedAmount.ShouldBe(Tnd(120m));
        deposit.AttachmentReference.ShouldBe("photo-retour-07.jpg");
        deposit.SettledInCashSessionId.ShouldBe(sessionId);
        var session = _harness.Session(sessionId);
        session.Movements.Select(m => m.Kind).ShouldBe([CashMovementKind.DepositReceived, CashMovementKind.DepositReturned, CashMovementKind.DepositRetained]);
        session.ExpectedDepositCash.ShouldBe(Money.Zero);
        session.ExpectedRevenueCash.ShouldBe(Tnd(220m));
    }

    [Fact]
    public async Task A_cash_deposit_received_by_another_open_drawer_is_handed_over_to_the_agent_who_returns_it()
    {
        var morning = _harness.User(Roles.Agent);
        var morningSession = await _harness.OpenSessionAsync(morning, openingFloat: 0m, cancellationToken: Ct);
        var id = (await _harness.Deposits(morning).TakeAsync(CashDeposit(Guid.CreateVersion7(), 500m), cancellationToken: Ct)).Value;
        var evening = _harness.User(Roles.Agent);
        var eveningSession = await _harness.OpenSessionAsync(evening, openingFloat: 0m, cancellationToken: Ct);

        (await _harness.Deposits(evening).ReleaseAsync(id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        var given = _harness.Session(morningSession);
        given.ExpectedDepositCash.ShouldBe(Money.Zero);
        given.Movements[^1].Kind.ShouldBe(CashMovementKind.DepositTransferOut);
        given.Movements[^1].RelatedSessionId.ShouldBe(eveningSession);
        given.Movements[^1].RecordedByUserId.ShouldBe(evening.UserId);
        var taker = _harness.Session(eveningSession);
        taker.Movements.Select(m => m.Kind).ShouldBe([CashMovementKind.DepositTransferIn, CashMovementKind.DepositReturned]);
        taker.ExpectedDepositCash.ShouldBe(Money.Zero);
    }

    [Fact]
    public async Task A_cash_deposit_of_a_closed_session_comes_from_the_safe_when_it_is_returned_days_later()
    {
        var agent = _harness.User(Roles.Agent);
        var departureSession = await _harness.OpenSessionAsync(agent, openingFloat: 0m, cancellationToken: Ct);
        var id = (await _harness.Deposits(agent).TakeAsync(CashDeposit(Guid.CreateVersion7(), 500m), cancellationToken: Ct)).Value;
        (await _harness.Sessions(agent).CloseAsync(departureSession, new CloseCashSessionRequest(0m, 500m), cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        _harness.Time.Advance(TimeSpan.FromDays(5));
        var returnSession = await _harness.OpenSessionAsync(agent, openingFloat: 50m, cancellationToken: Ct);

        (await _harness.Deposits(agent).RetainAsync(id, new DepositRetentionRequest(500m, "Sinistre responsable"), cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        _harness.Session(departureSession).Movements.ShouldHaveSingleItem("a closed session never changes");
        var session = _harness.Session(returnSession);
        session.Movements.Select(m => m.Kind).ShouldBe([CashMovementKind.DepositTransferIn, CashMovementKind.DepositRetained]);
        session.ExpectedDepositCash.ShouldBe(Money.Zero);
        session.ExpectedRevenueCash.ShouldBe(Tnd(550m));
        _harness.Deposit(id).Status.ShouldBe(DepositStatus.Retained);
    }

    [Fact]
    public async Task A_retention_is_validated()
    {
        var deposits = _harness.Deposits(_harness.User(Roles.Agent));
        var id = (await deposits.TakeAsync(CardImprint(Guid.CreateVersion7(), 300m), cancellationToken: Ct)).Value;

        (await deposits.RetainAsync(id, new DepositRetentionRequest(50m, " "), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.DepositJustificationRequired);
        (await deposits.RetainAsync(id, new DepositRetentionRequest(300.001m, "Dommages"), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.DepositRetainedAmountInvalid);
        (await deposits.RetainAsync(id, new DepositRetentionRequest(0m, "Dommages"), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.DepositRetainedAmountInvalid);
        (await deposits.RetainAsync(id, new DepositRetentionRequest(10.0001m, "Dommages"), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.DepositRetainedAmountInvalid);
        (await deposits.RetainAsync(Guid.CreateVersion7(), new DepositRetentionRequest(10m, "Dommages"), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.DepositNotFound);
        (await deposits.ReleaseAsync(Guid.CreateVersion7(), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.DepositNotFound);
        _harness.Deposit(id).IsHeld.ShouldBeTrue();
    }

    [Fact]
    public async Task Deposits_follow_the_rights_matrix()
    {
        var preparer = _harness.Deposits(_harness.User(Roles.Preparer));
        var id = (await _harness.Deposits(_harness.User(Roles.Accountant)).TakeAsync(CardImprint(Guid.CreateVersion7()), cancellationToken: Ct)).Value;

        (await preparer.TakeAsync(CardImprint(Guid.CreateVersion7()), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.Forbidden);
        (await preparer.ReleaseAsync(id, cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.Forbidden);
        (await preparer.RetainAsync(id, new DepositRetentionRequest(1m, "Dommages"), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.Forbidden);
        (await preparer.GetBookingDepositsAsync(Guid.CreateVersion7(), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.Forbidden);
        (await _harness.Deposits(_harness.User(Roles.Manager)).ReleaseAsync(id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Deposit_input_is_validated_before_any_write()
    {
        var deposits = _harness.Deposits(_harness.User(Roles.Agent));

        (await deposits.TakeAsync(CardImprint(Guid.CreateVersion7(), 10.0005m), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.AmountInvalid);
        (await deposits.TakeAsync(CardImprint(Guid.CreateVersion7()) with { ExternalReference = null }, cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.DepositReferenceRequired);
        (await deposits.TakeAsync(CardImprint(Guid.CreateVersion7()) with { AgencyId = Guid.CreateVersion7() }, cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.AgencyNotFound);
        _harness.Db.Rows<DepositHold>().ShouldBeEmpty();
    }
}
