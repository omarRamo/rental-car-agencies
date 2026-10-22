using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using CarRental.Infrastructure.Identity;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Payments;
using CarRental.Integration.Tests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Cash;

/// <summary>
/// Cash sessions on SQL Server: round trip with movements and temporal history, the unique session per cashier and day
/// under concurrency, concurrent movements on one drawer, the deposit handed over between two drawers, the totals query
/// and the demo seed.
/// </summary>
public sealed class CashSessionSqlTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_session_round_trips_with_its_movements_close_and_lock()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var agent = FinanceData.Actor(Roles.Agent);
        var sessionId = await FinanceData.OpenSessionAsync(host, agent, agencyId, 150.5m, Ct);
        await using (var scope = host.CreateScope(agent))
        {
            var sessions = scope.ServiceProvider.GetRequiredService<ICashSessions>();
            (await sessions.AddManualMovementAsync(sessionId, new ManualCashMovementRequest(CashFlowDirectionInfo.In, 20m, "Monnaie de la banque"), Ct)).IsSuccess.ShouldBeTrue();
            (await sessions.AddManualMovementAsync(sessionId, new ManualCashMovementRequest(CashFlowDirectionInfo.Out, 35.25m, "Fournitures"), Ct)).IsSuccess.ShouldBeTrue();
            (await sessions.CloseAsync(sessionId, new CloseCashSessionRequest(134m, 0m, "Pièce de 250 millimes égarée"), Ct)).Value
                .ShouldBe(new CashSessionClosing(Money.Tnd(-1.25m), Money.Zero));
        }

        await using (var scope = host.CreateScope(FinanceData.Actor(Roles.Accountant)))
        {
            (await scope.ServiceProvider.GetRequiredService<ICashSessions>().LockAsync(sessionId, Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var check = host.CreateScope(agent);
        var details = (await check.ServiceProvider.GetRequiredService<ICashSessions>().GetAsync(sessionId, Ct)).Value;
        details.Status.ShouldBe(CashSessionStatusInfo.Locked);
        details.BusinessDate.ShouldBe(TunisTime.DateOf(details.OpenedAtUtc));
        details.OpeningFloat.ShouldBe(Money.Tnd(150.5m));
        details.ExpectedRevenueCash.ShouldBe(Money.Tnd(135.25m));
        details.RevenueVariance.ShouldBe(Money.Tnd(-1.25m));
        details.Movements.Select(m => (m.Sequence, m.Kind, m.RevenueBalance.Amount)).ShouldBe(
            [(1, CashMovementKindInfo.ManualIn, 170.5m), (2, CashMovementKindInfo.ManualOut, 135.25m)]);
        details.LockedByName.ShouldNotBeNull();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var history = await db.Set<CashSession>().TemporalAll().IgnoreAutoIncludes().Where(s => s.Id == sessionId).Select(s => s.Status).ToListAsync(Ct);
        history.ShouldContain(CashSessionStatus.Open);
        history.ShouldContain(CashSessionStatus.Closed);
        history.ShouldContain(CashSessionStatus.Locked);
    }

    [Fact]
    public async Task The_database_keeps_one_session_per_cashier_agency_and_day_and_one_open_session_per_cashier()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var otherAgencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var cashierId = Guid.CreateVersion7();
        var now = new DateTimeOffset(2031, 5, 5, 9, 0, 0, TimeSpan.Zero);
        var first = CashSession.Open(agencyId, cashierId, "Caissier", Money.Zero, now).Value;
        await SaveAsync(first);

        var sameDay = CashSession.Open(agencyId, cashierId, "Caissier", Money.Zero, now.AddMinutes(1)).Value;
        sameDay.Close(Money.Zero, Money.Zero, null, AuditActor.System("system:tests"), now).IsSuccess.ShouldBeTrue();
        await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(sameDay));

        var secondOpen = CashSession.Open(otherAgencyId, cashierId, "Caissier", Money.Zero, now.AddDays(1)).Value;
        await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(secondOpen));
    }

    [Fact]
    public async Task Concurrent_openings_of_the_same_cashier_create_a_single_session()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var agent = FinanceData.Actor(Roles.Agent);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            await using var scope = host.CreateScope(agent);
            return await scope.ServiceProvider.GetRequiredService<ICashSessions>().OpenAsync(new OpenCashSessionRequest(agencyId, 10m + i), Ct);
        }));

        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Where(r => r.IsFailure).ShouldAllBe(r => r.Error == CashErrors.SessionAlreadyOpen || r.Error == CashErrors.SessionAlreadyExistsForDay);
        await using var check = host.CreateScope();
        (await check.ServiceProvider.GetRequiredService<IAppDbContext>().Set<CashSession>().CountAsync(s => s.CashierId == agent.UserId, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_cash_payments_into_one_drawer_never_lose_a_movement()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var agent = FinanceData.Actor(Roles.Agent);
        var sessionId = await FinanceData.OpenSessionAsync(host, agent, agencyId, 100m, Ct);
        var amounts = new[] { 10m, 20m, 30m, 40m, 50m };
        var bookingId = await TestBookings.NewAsync(host, agencyId, Ct);

        var results = await Task.WhenAll(amounts.Select(async amount =>
        {
            await using var scope = host.CreateScope(agent);
            var result = await scope.ServiceProvider.GetRequiredService<ICounterPayments>()
                .RecordPaymentAsync(new CounterPaymentRequest(bookingId, agencyId, PaymentMethodInfo.Cash, amount), Ct);
            return (Amount: amount, Result: result);
        }));

        results.Where(r => r.Result.IsFailure).ShouldAllBe(r => r.Result.Error == PaymentsErrors.ConcurrentUpdate);
        var accepted = results.Where(r => r.Result.IsSuccess).ToList();
        accepted.ShouldNotBeEmpty();
        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<IAppDbContext>();
        var session = await db.Set<CashSession>().AsNoTracking().SingleAsync(s => s.Id == sessionId, Ct);
        session.ExpectedRevenueCash.ShouldBe(Money.Tnd(100m + accepted.Sum(a => a.Amount)));
        session.Movements.Select(m => m.Sequence).Order().ShouldBe(Enumerable.Range(1, accepted.Count));
        session.Movements.Select(m => m.PaymentId!.Value).ShouldBe(accepted.Select(a => a.Result.Value), ignoreOrder: true);
    }

    [Fact]
    public async Task A_cash_deposit_taken_in_one_drawer_and_returned_from_another_moves_between_them()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var morning = FinanceData.Actor(Roles.Agent);
        var evening = FinanceData.Actor(Roles.Agent);
        var morningSession = await FinanceData.OpenSessionAsync(host, morning, agencyId, 0m, Ct);
        var eveningSession = await FinanceData.OpenSessionAsync(host, evening, agencyId, 0m, Ct);
        var bookingId = await TestBookings.NewAsync(host, agencyId, Ct);
        Guid depositId;
        await using (var scope = host.CreateScope(morning))
        {
            depositId = (await scope.ServiceProvider.GetRequiredService<IDeposits>()
                .TakeAsync(new DepositRequest(bookingId, agencyId, DepositModeInfo.Cash, 500m), Ct)).Value;
        }

        await using (var scope = host.CreateScope(evening))
        {
            (await scope.ServiceProvider.GetRequiredService<IDeposits>().RetainAsync(depositId, new DepositRetentionRequest(75m, "Carburant manquant"), Ct))
                .IsSuccess.ShouldBeTrue();
        }

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<IAppDbContext>();
        var given = await db.Set<CashSession>().AsNoTracking().SingleAsync(s => s.Id == morningSession, Ct);
        var taker = await db.Set<CashSession>().AsNoTracking().SingleAsync(s => s.Id == eveningSession, Ct);
        given.Movements.OrderBy(m => m.Sequence).Select(m => m.Kind).ShouldBe([CashMovementKind.DepositReceived, CashMovementKind.DepositTransferOut]);
        given.ExpectedDepositCash.ShouldBe(Money.Zero);
        taker.Movements.OrderBy(m => m.Sequence).Select(m => m.Kind).ShouldBe(
            [CashMovementKind.DepositTransferIn, CashMovementKind.DepositReturned, CashMovementKind.DepositRetained]);
        taker.ExpectedDepositCash.ShouldBe(Money.Zero);
        taker.ExpectedRevenueCash.ShouldBe(Money.Tnd(75m));
    }

    [Fact]
    public async Task The_session_totals_query_reads_sql_server()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var agent = FinanceData.Actor(Roles.Agent);
        await FinanceData.OpenSessionAsync(host, agent, agencyId, 100m, Ct);
        var bookingId = await TestBookings.NewAsync(host, agencyId, Ct);
        await using (var scope = host.CreateScope(agent))
        {
            var payments = scope.ServiceProvider.GetRequiredService<ICounterPayments>();
            var paymentId = (await payments.RecordPaymentAsync(new CounterPaymentRequest(bookingId, agencyId, PaymentMethodInfo.Cash, 250m), Ct)).Value;
            (await payments.RecordRefundAsync(new CounterRefundRequest(paymentId, PaymentMethodInfo.Cash, 50m, "Option retirée"), Ct)).IsSuccess.ShouldBeTrue();
            (await scope.ServiceProvider.GetRequiredService<IDeposits>().TakeAsync(new DepositRequest(bookingId, agencyId, DepositModeInfo.Cash, 400m), Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var check = host.CreateScope();
        var today = TunisTime.Today(TimeProvider.System);
        var totals = (await check.ServiceProvider.GetRequiredService<ICashQueries>().GetSessionTotalsAsync(today.AddDays(-1), today.AddDays(1), agencyId, Ct)).ShouldHaveSingleItem();

        totals.CashierId.ShouldBe(agent.UserId!.Value);
        totals.Totals.PaymentsReceived.ShouldBe(Money.Tnd(250m));
        totals.Totals.RefundsPaid.ShouldBe(Money.Tnd(50m));
        totals.Totals.DepositsReceived.ShouldBe(Money.Tnd(400m));
        totals.ExpectedRevenueCash.ShouldBe(Money.Tnd(300m));
        totals.ExpectedDepositCash.ShouldBe(Money.Tnd(400m));
    }

    [Fact]
    public async Task The_demo_agent_has_a_locked_session_and_an_open_session()
    {
        await using var scope = host.CreateScope();
        var agent = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(DemoUsers.AgentEmail);
        agent.ShouldNotBeNull();

        var sessions = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<CashSession>()
            .AsNoTracking()
            .Where(s => s.CashierId == agent.Id)
            .ToListAsync(Ct);

        var locked = sessions.Where(s => s.Status == CashSessionStatus.Locked).ShouldHaveSingleItem();
        locked.HasVariance.ShouldBeTrue();
        locked.VarianceJustification.ShouldNotBeNullOrWhiteSpace();
        locked.Movements.Count.ShouldBe(2);
        sessions.ShouldContain(s => s.Status == CashSessionStatus.Open && s.BusinessDate == locked.BusinessDate.AddDays(1));
    }

    private async Task SaveAsync(CashSession session)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        db.Set<CashSession>().Add(session);
        await db.SaveChangesAsync(Ct);
    }
}
