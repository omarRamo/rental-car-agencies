using CarRental.Application.Common.Persistence;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Domain.Payments;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Payments;

/// <summary>
/// Payments and deposits on SQL Server: one transaction for a cash payment and its cash movement, rollback of a failed
/// refund, temporal history, millimes, audit log and outbox, and the totals of the reconciliations.
/// </summary>
public sealed class PaymentsPersistenceTests(IntegrationHost host)
{
    private static readonly AuditActor Seed = AuditActor.System("system:tests");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_cash_payment_and_its_cash_movement_are_saved_together_with_audit_and_outbox()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var agent = FinanceData.Actor(Roles.Agent);
        var sessionId = await FinanceData.OpenSessionAsync(host, agent, agencyId, 100m, Ct);
        var bookingId = Guid.CreateVersion7();

        Guid paymentId;
        await using (var scope = host.CreateScope(agent))
        {
            var recorded = await scope.ServiceProvider.GetRequiredService<ICounterPayments>()
                .RecordPaymentAsync(new CounterPaymentRequest(bookingId, agencyId, PaymentMethodInfo.Cash, 1234.567m, Notes: "Solde"), Ct);
            recorded.IsSuccess.ShouldBeTrue(recorded.IsFailure ? recorded.Error.ToString() : null);
            paymentId = recorded.Value;
        }

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var payment = await db.Set<Payment>().AsNoTracking().SingleAsync(p => p.Id == paymentId, Ct);
        payment.Amount.ShouldBe(Money.Tnd(1234.567m));
        payment.CapturedAmount.ShouldBe(Money.Tnd(1234.567m));
        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.CashSessionId.ShouldBe(sessionId);
        payment.RecordedByUserId.ShouldBe(agent.UserId);
        payment.RecordedByName.ShouldBe(agent.UserName);
        var session = await db.Set<CashSession>().AsNoTracking().SingleAsync(s => s.Id == sessionId, Ct);
        session.ExpectedRevenueCash.ShouldBe(Money.Tnd(1334.567m));
        var movement = session.Movements.ShouldHaveSingleItem("the movements are loaded with the session");
        movement.Kind.ShouldBe(CashMovementKind.PaymentReceived);
        movement.PaymentId.ShouldBe(paymentId);
        movement.BookingId.ShouldBe(bookingId);
        movement.Sequence.ShouldBe(1);

        var audited = await db.Set<AuditLog>().AsNoTracking()
            .Where(a => a.Action == AuditActions.EntityCreated && (a.EntityId == paymentId.ToString() || a.EntityId == movement.Id.ToString()))
            .Select(a => new { a.EntityType, a.UserId })
            .ToListAsync(Ct);
        audited.Select(a => a.EntityType).ShouldBe(["Payment", "CashMovement"], ignoreOrder: true);
        audited.ShouldAllBe(a => a.UserId == agent.UserId);
        var messages = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Payload.Contains(paymentId.ToString()))
            .Select(m => m.Type)
            .ToListAsync(Ct);
        messages.ShouldContain(typeof(PaymentRecorded).FullName!);
    }

    [Fact]
    public async Task A_refund_the_drawer_cannot_pay_rolls_back_entirely()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var agent = FinanceData.Actor(Roles.Agent);
        await FinanceData.OpenSessionAsync(host, agent, agencyId, 10m, Ct);
        Guid paymentId;
        await using (var scope = host.CreateScope(agent))
        {
            var payments = scope.ServiceProvider.GetRequiredService<ICounterPayments>();
            paymentId = (await payments.RecordPaymentAsync(new CounterPaymentRequest(Guid.CreateVersion7(), agencyId, PaymentMethodInfo.CardTerminal, 500m), Ct)).Value;
            (await payments.RecordRefundAsync(new CounterRefundRequest(paymentId, PaymentMethodInfo.Cash, 50m, "Trop-perçu"), Ct))
                .Error.ShouldBe(CashErrors.InsufficientCash);
        }

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<IAppDbContext>();
        var original = await db.Set<Payment>().AsNoTracking().SingleAsync(p => p.Id == paymentId, Ct);
        original.RefundedAmount.ShouldBe(Money.Zero, "the refunded amount set before the drawer refused is rolled back");
        original.Status.ShouldBe(PaymentStatus.Captured);
        (await db.Set<Payment>().CountAsync(p => p.OriginalPaymentId == paymentId, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Payments_and_deposits_keep_their_history_in_temporal_tables()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var accountant = FinanceData.Actor(Roles.Accountant);
        Guid paymentId;
        Guid depositId;
        await using (var scope = host.CreateScope(accountant))
        {
            var payments = scope.ServiceProvider.GetRequiredService<ICounterPayments>();
            var deposits = scope.ServiceProvider.GetRequiredService<IDeposits>();
            paymentId = (await payments.RecordPaymentAsync(new CounterPaymentRequest(Guid.CreateVersion7(), agencyId, PaymentMethodInfo.BankTransfer, 600m, "VIR-1", AwaitingConfirmation: true), Ct)).Value;
            (await payments.ConfirmPendingAsync(paymentId, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
            depositId = (await deposits.TakeAsync(new DepositRequest(Guid.CreateVersion7(), agencyId, DepositModeInfo.CardImprint, 800m, "AUT-1"), Ct)).Value;
            (await deposits.RetainAsync(depositId, new DepositRetentionRequest(150.5m, "Rayure pare-chocs", "devis-42.pdf"), Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var statuses = await db.Set<Payment>().TemporalAll().Where(p => p.Id == paymentId).Select(p => p.Status).ToListAsync(Ct);
        statuses.ShouldBe([PaymentStatus.Pending, PaymentStatus.Captured], ignoreOrder: true);
        var deposit = await db.Set<DepositHold>().AsNoTracking().SingleAsync(d => d.Id == depositId, Ct);
        deposit.Status.ShouldBe(DepositStatus.PartiallyRetained);
        deposit.RetainedAmount.ShouldBe(Money.Tnd(150.5m));
        deposit.RetentionJustification.ShouldBe("Rayure pare-chocs");
        deposit.AttachmentReference.ShouldBe("devis-42.pdf");
        deposit.SettledByUserId.ShouldBe(accountant.UserId);
        (await db.Set<DepositHold>().TemporalAll().CountAsync(d => d.Id == depositId, Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task The_reconciliation_totals_are_read_from_sql_server()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var bookingId = Guid.CreateVersion7();

        // 15/03/2027 09:00 Tunis, then 23:30 UTC = 16/03 00:30 Tunis.
        var morning = new DateTimeOffset(2027, 3, 15, 8, 0, 0, TimeSpan.Zero);
        var afterMidnight = new DateTimeOffset(2027, 3, 15, 23, 30, 0, TimeSpan.Zero);
        var card = Payment.RecordCounter(new CounterPaymentDetails(agencyId, bookingId, PaymentMethod.CardTerminal, Money.Tnd(300m)), Seed, morning).Value;
        var cheque = Payment.RecordCounter(new CounterPaymentDetails(agencyId, bookingId, PaymentMethod.Cheque, Money.Tnd(120.25m), "CHQ-1"), Seed, afterMidnight).Value;
        var pending = Payment.RecordCounter(new CounterPaymentDetails(agencyId, bookingId, PaymentMethod.BankTransfer, Money.Tnd(999m), "VIR-1", AwaitingConfirmation: true), Seed, morning).Value;
        var refund = card.RecordRefund(new CounterRefundDetails(PaymentMethod.CardTerminal, Money.Tnd(45.5m), "Geste commercial"), Seed, afterMidnight).Value;
        var held = DepositHold.Take(new DepositDetails(agencyId, bookingId, DepositMode.CardImprint, Money.Tnd(800m), "AUT-1"), Seed, morning).Value;
        var cheque2 = DepositHold.Take(new DepositDetails(agencyId, bookingId, DepositMode.Cheque, Money.Tnd(400m), "CHQ-D"), Seed, morning).Value;
        var released = DepositHold.Take(new DepositDetails(agencyId, Guid.CreateVersion7(), DepositMode.Cheque, Money.Tnd(300m), "CHQ-2"), Seed, morning).Value;
        released.Release(null, Seed, afterMidnight).IsSuccess.ShouldBeTrue();
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Payment>().AddRange(card, cheque, pending, refund);
            db.Set<DepositHold>().AddRange(held, cheque2, released);
            await db.SaveChangesAsync(Ct);
        }

        await using var check = host.CreateScope();
        var queries = check.ServiceProvider.GetRequiredService<IPaymentsQueries>();
        var daily = await queries.GetDailyTotalsByMethodAsync(new DateOnly(2027, 3, 15), new DateOnly(2027, 3, 16), agencyId, Ct);
        var liabilities = await queries.GetOpenDepositLiabilitiesAsync(agencyId, Ct);
        var summary = await queries.GetBookingSummaryAsync(bookingId, Ct);

        daily.ShouldBe(
        [
            new DailyPaymentTotal(new DateOnly(2027, 3, 15), PaymentMethodInfo.CardTerminal, Money.Tnd(300m), Money.Zero, 1, 0),
            new DailyPaymentTotal(new DateOnly(2027, 3, 16), PaymentMethodInfo.CardTerminal, Money.Zero, Money.Tnd(45.5m), 0, 1),
            new DailyPaymentTotal(new DateOnly(2027, 3, 16), PaymentMethodInfo.Cheque, Money.Tnd(120.25m), Money.Zero, 1, 0),
        ]);
        liabilities.Total.ShouldBe(Money.Tnd(1200m));
        liabilities.ByMode.ShouldBe(
        [
            new DepositLiabilityByMode(DepositModeInfo.CardImprint, Money.Tnd(800m), 1),
            new DepositLiabilityByMode(DepositModeInfo.Cheque, Money.Tnd(400m), 1),
        ]);
        summary.Payments.ShouldBe(new PaymentTotals(Money.Tnd(420.25m), Money.Tnd(45.5m), Money.Tnd(374.75m), Money.Tnd(999m), Money.Zero));
        summary.Deposits.OpenLiability.ShouldBe(Money.Tnd(1200m), "deposits never count as paid (invariant 8)");
    }

    [Fact]
    public async Task Two_concurrent_refunds_of_the_same_payment_cannot_exceed_what_was_captured()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var manager = FinanceData.Actor(Roles.Manager);
        Guid paymentId;
        await using (var scope = host.CreateScope(manager))
        {
            paymentId = (await scope.ServiceProvider.GetRequiredService<ICounterPayments>()
                .RecordPaymentAsync(new CounterPaymentRequest(Guid.CreateVersion7(), agencyId, PaymentMethodInfo.CardTerminal, 100m), Ct)).Value;
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var scope = host.CreateScope(manager);
            return await scope.ServiceProvider.GetRequiredService<ICounterPayments>()
                .RecordRefundAsync(new CounterRefundRequest(paymentId, PaymentMethodInfo.CardTerminal, 60m, "Annulation"), Ct);
        }));

        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Where(r => r.IsFailure).ShouldAllBe(r => r.Error == PaymentsErrors.ConcurrentUpdate || r.Error == PaymentsErrors.RefundExceedsCaptured);
        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<IAppDbContext>();
        (await db.Set<Payment>().AsNoTracking().SingleAsync(p => p.Id == paymentId, Ct)).RefundedAmount.ShouldBe(Money.Tnd(60m));
        (await db.Set<Payment>().CountAsync(p => p.OriginalPaymentId == paymentId, Ct)).ShouldBe(1);
    }
}
