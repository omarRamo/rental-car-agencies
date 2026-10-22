using CarRental.Application.Accounting.Closing;
using CarRental.Application.Accounting.Contracts;
using CarRental.Application.Accounting.Periods;
using CarRental.Application.Accounting.Reconciliation;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Cash.Expenses;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Payments;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Accounting;

/// <summary>
/// The monthly closing on SQL Server (ADR-0065): a closed month refuses the facts dated in it until a manager reopens it
/// (audited, temporal history); the lock of a month is shared by the writers and exclusive for its closing.
/// </summary>
public sealed class AccountingPeriodSqlTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AccountingMonth LastMonth => AccountingMonth.Of(TunisTime.Today(TimeProvider.System)).Previous();

    [Fact]
    public async Task A_closed_month_refuses_the_facts_dated_in_it_until_a_manager_reopens_it()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var accountant = FinanceData.Actor(Roles.Accountant);
        var manager = FinanceData.Actor(Roles.Manager);
        var month = LastMonth;
        var day = month.FirstDay.AddDays(14);

        var closingId = await RunAsync<MonthlyClosingService, Result<Guid>>(accountant, s => s.CloseAsync(agencyId, month, [], Ct));

        closingId.IsSuccess.ShouldBeTrue(closingId.IsFailure ? closingId.Error.ToString() : null);
        (await RunAsync<ExpenseService, Result<Guid>>(accountant, s => s.RecordAsync(Form(agencyId, day), Ct))).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await RunAsync<ReconciliationService, Result<Guid>>(accountant, s => s.AddManualLineAsync(agencyId, new StatementLineForm(day, "SEPT-1", StatementLineKind.Payment, 10m, null, null, null), Ct)))
            .Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await RunAsync<IAccountingPeriods, Result>(accountant, p => p.EnsureOpenAsync(agencyId, day, Ct))).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await RunAsync<IAccountingPeriods, Result>(accountant, p => p.EnsureOpenAsync(agencyId, month.Next().FirstDay, Ct))).IsSuccess.ShouldBeTrue();
        (await RunAsync<IAccountingPeriods, IReadOnlyList<DateOnly>>(accountant, p => p.GetClosedMonthsAsync(agencyId, Ct))).ShouldBe([month.FirstDay]);
        (await RunAsync<MonthlyClosingService, Result>(accountant, s => s.ReopenAsync(agencyId, month, "Facture fournisseur oubliée", Ct))).Error.ShouldBe(AccountingErrors.Forbidden);

        (await RunAsync<MonthlyClosingService, Result>(manager, s => s.ReopenAsync(agencyId, month, "Facture fournisseur oubliée", Ct))).IsSuccess.ShouldBeTrue();

        (await RunAsync<ExpenseService, Result<Guid>>(accountant, s => s.RecordAsync(Form(agencyId, day), Ct))).IsSuccess.ShouldBeTrue();
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var period = await db.Set<AccountingPeriod>().AsNoTracking().SingleAsync(p => p.AgencyId == agencyId, Ct);
        (period.Status, period.ReopenCount, period.ReopenReason, period.ReopenedByName).ShouldBe((AccountingPeriodStatus.Open, 1, "Facture fournisseur oubliée", manager.UserName));
        (await db.Set<PeriodClosing>().AsNoTracking().CountAsync(c => c.AgencyId == agencyId, Ct)).ShouldBe(1, "the closing is kept");
        (await db.Set<AccountingPeriod>().TemporalAll().CountAsync(p => p.Id == period.Id, Ct)).ShouldBeGreaterThanOrEqualTo(2);
        var key = period.Id.ToString();
        var actions = await db.Set<AuditLog>().AsNoTracking().Where(a => a.EntityId == key).Select(a => a.Action).ToListAsync(Ct);
        actions.ShouldContain("accounting.period_closed");
        actions.ShouldContain("accounting.period_reopened");
    }

    [Fact]
    public async Task The_lock_of_a_month_is_shared_by_the_writers_and_exclusive_for_its_closing()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var month = AccountingMonth.Of(TunisTime.Today(TimeProvider.System));
        var resource = AccountingLocks.Period(agencyId, month);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A writer dates a fact in the month: it holds the shared lock of the month until its transaction ends.
        var writer = Task.Run(async () =>
        {
            await using var scope = host.CreateScope(FinanceData.Actor(Roles.Accountant));
            var periods = scope.ServiceProvider.GetRequiredService<IAccountingPeriods>();
            return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(
                async ct =>
                {
                    var open = await periods.EnsureOpenAsync(agencyId, month.FirstDay, ct);
                    held.SetResult();
                    await release.Task.WaitAsync(ct);
                    return open;
                },
                cancellationToken: Ct);
        }, Ct);
        await held.Task.WaitAsync(Ct);

        (await TryLockAsync(resource, "Exclusive")).ShouldBeLessThan(0, "a closing waits for the writers in progress");
        (await TryLockAsync(resource, "Shared")).ShouldBeGreaterThanOrEqualTo(0, "the writers do not wait for each other");
        (await TryLockAsync(AccountingLocks.Period(agencyId, month.Next()), "Exclusive")).ShouldBeGreaterThanOrEqualTo(0, "each month has its own lock");

        release.SetResult();
        (await writer).IsSuccess.ShouldBeTrue();
        (await TryLockAsync(resource, "Exclusive")).ShouldBeGreaterThanOrEqualTo(0, "once the writer committed");
    }

    private static ExpenseForm Form(Guid agencyId, DateOnly day) =>
        new(agencyId, day, ExpenseCategoryInfo.Fuel, "Station Agil", ExpenseAmountMode.InclVatAndRate, 59.5m, null, null, 0.19m, ExpensePaymentMethodInfo.BankTransfer, "VIR-SEPT");

    private async Task<T> RunAsync<TService, T>(UserContextSnapshot actor, Func<TService, Task<T>> call)
        where TService : notnull
    {
        await using var scope = host.CreateScope(actor);
        return await call(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>Asks the lock without waiting from another connection, in a transaction rolled back at once (the result of sp_getapplock).</summary>
    private async Task<int> TryLockAsync(string resource, string mode)
    {
        await using var connection = new SqlConnection(host.Factory.SqlConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = @resource, @LockMode = @mode, @LockOwner = 'Transaction', @LockTimeout = 0;
            SELECT @result;
            """;
        command.Parameters.AddWithValue("@resource", resource);
        command.Parameters.AddWithValue("@mode", mode);
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
        await transaction.RollbackAsync(Ct);
        return result;
    }
}
