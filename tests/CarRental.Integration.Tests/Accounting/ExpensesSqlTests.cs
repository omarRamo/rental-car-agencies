using System.Globalization;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Cash.Expenses;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Paging;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Cash;
using CarRental.Domain.Cash.Expenses;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Accounting;

/// <summary>
/// The expenses on SQL Server and Azurite (ADR-0063): numbers given once under concurrency, the receipt in the private
/// container, a cash expense and its deletion moving the cash of the session in the same transaction, the history kept.
/// </summary>
public sealed class ExpensesSqlTests(IntegrationHost host)
{
    private static readonly byte[] Pdf = "%PDF-1.7\n% receipt of the tests\n"u8.ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => TunisTime.Today(TimeProvider.System);

    [Fact]
    public async Task Expenses_recorded_at_once_take_each_their_own_number_and_their_receipt_lands_in_the_private_container()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var accountant = FinanceData.Actor(Roles.Accountant);

        var recorded = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var scope = host.CreateScope(accountant);
            return await scope.ServiceProvider.GetRequiredService<ExpenseService>().RecordAsync(BankForm(agencyId, 10m + i), Ct);
        }));

        recorded.ShouldAllBe(r => r.IsSuccess);
        await using var read = host.CreateScope(accountant);
        var db = read.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var numbers = await db.Set<Expense>().AsNoTracking().Where(e => e.AgencyId == agencyId).Select(e => e.Number).ToListAsync(Ct);
        numbers.Order(StringComparer.Ordinal).ShouldBe(Enumerable.Range(1, 8).Select(n => string.Create(CultureInfo.InvariantCulture, $"DEP-{Today.Year:D4}-{n:D6}")));

        var expenses = read.ServiceProvider.GetRequiredService<ExpenseService>();
        (await expenses.UploadReceiptAsync(recorded[0].Value, "facture.pdf", new MemoryStream(Pdf), Ct)).IsSuccess.ShouldBeTrue();
        var receipt = (await expenses.FindReceiptAsync(recorded[0].Value, Ct)).Value;
        receipt.Container.ShouldBe(FileContainers.Invoices);
        receipt.Path.ShouldStartWith($"expenses/{Today.Year:D4}/{recorded[0].Value:N}/");
        receipt.ContentType.ShouldBe("application/pdf");
        await using var stored = (await read.ServiceProvider.GetRequiredService<IFileStorage>().OpenReadAsync(receipt.Container, receipt.Path, Ct)).ShouldNotBeNull();
        using var copy = new MemoryStream();
        await stored.Content.CopyToAsync(copy, Ct);
        copy.ToArray().ShouldBe(Pdf);
        var view = (await expenses.GetAsync(recorded[0].Value, Ct)).Value;
        (view.HasReceipt, view.ReceiptFileName, view.Revision).ShouldBe((true, "facture.pdf", 2));
    }

    [Fact]
    public async Task A_cash_expense_and_its_deletion_move_the_cash_of_the_session_and_stay_in_the_history()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var accountant = FinanceData.Actor(Roles.Accountant);
        var sessionId = await FinanceData.OpenSessionAsync(host, accountant, agencyId, 100m, Ct);
        Guid expenseId;
        await using (var scope = host.CreateScope(accountant))
        {
            expenseId = (await scope.ServiceProvider.GetRequiredService<ExpenseService>().RecordAsync(
                new ExpenseForm(agencyId, Today, ExpenseCategoryInfo.Other, "Droguerie du Lac", ExpenseAmountMode.AsInvoiced, null, 25m, 4.75m, null, ExpensePaymentMethodInfo.Cash),
                Ct)).Value;
        }

        await using (var scope = host.CreateScope(accountant))
        {
            var session = (await scope.ServiceProvider.GetRequiredService<ICashSessions>().GetAsync(sessionId, Ct)).Value;
            session.ExpectedRevenueCash.ShouldBe(Money.Tnd(70.25m));
            session.Movements.ShouldHaveSingleItem().Kind.ShouldBe(CashMovementKindInfo.ExpensePaid);
            (await scope.ServiceProvider.GetRequiredService<ExpenseService>().DeleteAsync(expenseId, "Payée deux fois par erreur", Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var read = host.CreateScope(accountant);
        var db = read.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var movements = await db.Set<CashMovement>().AsNoTracking().Where(m => m.SessionId == sessionId).OrderBy(m => m.Sequence).ToListAsync(Ct);
        movements.Select(m => (m.Kind, m.ExpenseId)).ShouldBe([(CashMovementKind.ExpensePaid, (Guid?)expenseId), (CashMovementKind.ExpenseReversed, (Guid?)expenseId)]);
        (await db.Set<CashSession>().AsNoTracking().SingleAsync(s => s.Id == sessionId, Ct)).ExpectedRevenueCash.ShouldBe(Money.Tnd(100m));
        (await db.Set<Expense>().AsNoTracking().AnyAsync(e => e.Id == expenseId, Ct)).ShouldBeFalse("a deleted expense is filtered out");
        var deleted = await db.Set<Expense>().AsNoTracking().IgnoreQueryFilters().SingleAsync(e => e.Id == expenseId, Ct);
        (deleted.IsDeleted, deleted.DeletionReason, deleted.ReversedInSessionId).ShouldBe((true, "Payée deux fois par erreur", (Guid?)sessionId));
        (await db.Set<Expense>().TemporalAll().IgnoreQueryFilters().CountAsync(e => e.Id == expenseId, Ct)).ShouldBeGreaterThanOrEqualTo(2, "the temporal history keeps the expense as recorded");
        var key = expenseId.ToString();
        (await db.Set<AuditLog>().AsNoTracking().Where(a => a.EntityType == nameof(Expense) && a.EntityId == key).Select(a => a.Action).ToListAsync(Ct))
            .ShouldBe([AuditActions.EntityCreated, AuditActions.EntitySoftDeleted], ignoreOrder: true);
        var list = (await read.ServiceProvider.GetRequiredService<ExpenseService>().ListAsync(new ExpenseFilter(agencyId, IncludeDeleted: true), new PageRequest(1, 25), Ct)).Value;
        list.Page.Items.ShouldHaveSingleItem().IsDeleted.ShouldBeTrue();
        list.Count.ShouldBe(0, "a deleted expense counts in no total");
    }

    private static ExpenseForm BankForm(Guid agencyId, decimal inclVat) =>
        new(agencyId, Today, ExpenseCategoryInfo.Fuel, "Station Agil", ExpenseAmountMode.InclVatAndRate, inclVat, null, null, 0.19m, ExpensePaymentMethodInfo.BankTransfer, "VIR-EXP");
}
