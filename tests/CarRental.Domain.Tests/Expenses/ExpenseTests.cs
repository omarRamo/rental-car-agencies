using CarRental.Domain.Cash;
using CarRental.Domain.Cash.Expenses;
using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Expenses;

/// <summary>Expenses: recording, changes until the closing (cash ones keep their amounts), logical deletion, cash movements and the cost rule.</summary>
public sealed class ExpenseTests
{
    private static readonly Guid AgencyId = Guid.CreateVersion7();
    private static readonly AuditActor Accountant = new(Guid.CreateVersion7(), "comptable@demo.local", null, null);
    private static readonly DateTimeOffset At = new(2027, 3, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2027, 3, 15);
    private static readonly ExpenseNumber Number = new(2027, 12);

    [Fact]
    public void An_expense_is_recorded_with_its_number_amounts_and_trimmed_texts()
    {
        var expense = Expense.Record(Details(supplier: "  Station Agil  ", reference: " F-123 "), Number, null, Today, Accountant, At).Value;

        expense.Number.ShouldBe("DEP-2027-000012");
        (expense.Year, expense.Sequence).ShouldBe((2027, 12));
        expense.Supplier.ShouldBe("Station Agil");
        expense.Reference.ShouldBe("F-123");
        expense.ExclVat.ShouldBe(Money.Tnd(100m));
        expense.Vat.ShouldBe(Money.Tnd(19m));
        expense.InclVat.ShouldBe(Money.Tnd(119m));
        expense.VatRate.ShouldBe(0.19m);
        expense.Revision.ShouldBe(1);
        expense.RecordedByName.ShouldBe("comptable@demo.local");
        expense.IsPaidInCash.ShouldBeFalse();
        expense.DomainEvents.ShouldHaveSingleItem().ShouldBe(new ExpenseRecorded(expense.Id, AgencyId, ExpenseCategory.Fuel, ExpensePaymentMethod.BankTransfer));
    }

    [Fact]
    public void The_details_are_checked()
    {
        Record(Details() with { Date = Today.AddDays(1) }).Error.ShouldBe(ExpenseErrors.DateInvalid);
        Record(Details() with { Date = new DateOnly(1999, 12, 31) }).Error.ShouldBe(ExpenseErrors.DateInvalid);
        Record(Details() with { Category = (ExpenseCategory)99 }).Error.ShouldBe(ExpenseErrors.CategoryInvalid);
        Record(Details(supplier: "X")).Error.ShouldBe(ExpenseErrors.SupplierInvalid);
        Record(Details(supplier: new string('x', 121))).Error.ShouldBe(ExpenseErrors.SupplierInvalid);
        Record(Details(reference: new string('x', 61))).Error.ShouldBe(ExpenseErrors.TextTooLong);
        Record(Details() with { PaymentMethod = ExpensePaymentMethod.Cheque }).Error.ShouldBe(ExpenseErrors.ChequeNumberRequired);
        Record(Details() with { PaymentMethod = (ExpensePaymentMethod)9 }).Error.ShouldBe(ExpenseErrors.PaymentMethodInvalid);
        Record(Details() with { Category = ExpenseCategory.PremisesRent, VehicleId = Guid.CreateVersion7() }).Error.ShouldBe(ExpenseErrors.VehicleNotAllowed);
        Record(Details() with { MaintenanceEventId = Guid.CreateVersion7() }).Error.ShouldBe(ExpenseErrors.LinkInvalid);
        Record(Details() with { VehicleId = Guid.CreateVersion7(), MaintenanceEventId = Guid.CreateVersion7(), ClaimId = Guid.CreateVersion7() })
            .Error.ShouldBe(ExpenseErrors.LinkInvalid);
        Expense.Record(Details() with { AgencyId = Guid.Empty }, Number, null, Today, Accountant, At).Error.ShouldBe(ExpenseErrors.AgencyNotFound);
        Expense.Record(Details() with { PaymentMethod = ExpensePaymentMethod.Cash }, Number, null, Today, Accountant, At).Error.ShouldBe(ExpenseErrors.CashSessionRequired);
    }

    [Fact]
    public void A_bank_expense_changes_until_its_month_is_closed_and_refuses_a_stale_version()
    {
        var expense = Record(Details()).Value;
        var changed = Details(supplier: "Garage Ennour") with
        {
            Category = ExpenseCategory.Repairs,
            Amounts = ExpenseAmounts.FromInvoice(Money.Tnd(200m), Money.Tnd(38m)).Value,
            Date = Today.AddDays(-3),
            PaymentMethod = ExpensePaymentMethod.Cheque,
            PaymentReference = "CHQ 1234",
        };

        expense.Change(changed, revision: 1, Today, Accountant, At).IsSuccess.ShouldBeTrue();

        expense.Supplier.ShouldBe("Garage Ennour");
        expense.InclVat.ShouldBe(Money.Tnd(238m));
        expense.Date.ShouldBe(Today.AddDays(-3));
        expense.PaymentMethod.ShouldBe(ExpensePaymentMethod.Cheque);
        expense.Revision.ShouldBe(2);
        expense.Change(changed, revision: 1, Today, Accountant, At).Error.ShouldBe(ExpenseErrors.Stale);
        expense.Change(changed with { PaymentMethod = ExpensePaymentMethod.Cash }, revision: 2, Today, Accountant, At).Error.ShouldBe(ExpenseErrors.PaymentMethodChangeNotAllowed);
        expense.Change(changed with { AgencyId = Guid.CreateVersion7() }, revision: 2, Today, Accountant, At).Error.ShouldBe(ExpenseErrors.AgencyNotFound);
    }

    [Fact]
    public void A_cash_expense_keeps_its_date_amounts_and_means_but_its_texts_change()
    {
        var expense = Expense.Record(Details() with { PaymentMethod = ExpensePaymentMethod.Cash }, Number, Guid.CreateVersion7(), Today, Accountant, At).Value;
        var cash = Details() with { PaymentMethod = ExpensePaymentMethod.Cash };

        expense.Change(cash with { Amounts = ExpenseAmounts.FromInclVat(Money.Tnd(120m), 0.19m).Value }, 1, Today, Accountant, At).Error.ShouldBe(ExpenseErrors.CashAmountsLocked);
        expense.Change(cash with { Date = Today.AddDays(-1) }, 1, Today, Accountant, At).Error.ShouldBe(ExpenseErrors.CashAmountsLocked);
        expense.Change(cash with { PaymentMethod = ExpensePaymentMethod.Card }, 1, Today, Accountant, At).Error.ShouldBe(ExpenseErrors.CashAmountsLocked);

        // The same amounts typed as on the invoice: accepted, the rate entered first is kept.
        var sameAsInvoice = cash with { Amounts = ExpenseAmounts.FromInvoice(Money.Tnd(100m), Money.Tnd(19m)).Value, Description = "Plein du véhicule 3" };
        expense.Change(sameAsInvoice, 1, Today, Accountant, At).IsSuccess.ShouldBeTrue();
        expense.Description.ShouldBe("Plein du véhicule 3");
        expense.VatRate.ShouldBe(0.19m);
        expense.PaymentReference.ShouldBeNull("cash has no payment reference");
    }

    [Fact]
    public void Deleting_needs_a_reason_and_keeps_who_when_and_why()
    {
        var expense = Record(Details()).Value;

        expense.Delete("Non", null, Accountant, At).Error.ShouldBe(ExpenseErrors.DeletionReasonRequired);
        expense.Delete("Saisie en double", null, Accountant, At.AddHours(1)).IsSuccess.ShouldBeTrue();

        expense.IsDeleted.ShouldBeTrue();
        expense.DeletedAtUtc.ShouldBe(At.AddHours(1));
        expense.DeletedByName.ShouldBe("comptable@demo.local");
        expense.DeletionReason.ShouldBe("Saisie en double");
        expense.Delete("Encore une fois", null, Accountant, At).Error.ShouldBe(ExpenseErrors.Deleted);
        expense.Change(Details(), expense.Revision, Today, Accountant, At).Error.ShouldBe(ExpenseErrors.Deleted);
        expense.AttachReceipt(new ExpenseReceipt("p", "image/png", 10, "hash", null), Accountant, At).Error.ShouldBe(ExpenseErrors.Deleted);

        var cash = Expense.Record(Details() with { PaymentMethod = ExpensePaymentMethod.Cash }, Number, Guid.CreateVersion7(), Today, Accountant, At).Value;
        cash.Delete("Saisie en double", null, Accountant, At).Error.ShouldBe(ExpenseErrors.CashSessionRequired);
        var reversal = Guid.CreateVersion7();
        cash.Delete("Saisie en double", reversal, Accountant, At).IsSuccess.ShouldBeTrue();
        cash.ReversedInSessionId.ShouldBe(reversal);
    }

    [Fact]
    public void A_receipt_is_attached_or_replaced_with_its_metadata()
    {
        var expense = Record(Details()).Value;

        expense.AttachReceipt(new ExpenseReceipt("expenses/2027/a/b.pdf", "application/pdf", 1234, "abcd", @"C:\scans\ticket.pdf"), Accountant, At).IsSuccess.ShouldBeTrue();

        expense.HasReceipt.ShouldBeTrue();
        expense.ReceiptPath.ShouldBe("expenses/2027/a/b.pdf");
        expense.ReceiptLength.ShouldBe(1234);
        expense.ReceiptUploadedBy.ShouldBe("comptable@demo.local");
        expense.AttachReceipt(new ExpenseReceipt(" ", "application/pdf", 1, "h", null), Accountant, At).Error.ShouldBe(ExpenseErrors.ReceiptInvalid);
        expense.AttachReceipt(new ExpenseReceipt("p", "application/pdf", 0, "h", null), Accountant, At).Error.ShouldBe(ExpenseErrors.ReceiptInvalid);
    }

    [Theory]
    [InlineData(ExpenseCategory.Fuel, true, false, false, ExpenseVehicleCostRule.Counted)]
    [InlineData(ExpenseCategory.Repairs, true, false, false, ExpenseVehicleCostRule.Counted)]
    [InlineData(ExpenseCategory.Maintenance, true, false, false, ExpenseVehicleCostRule.Counted)]
    [InlineData(ExpenseCategory.Other, true, false, false, ExpenseVehicleCostRule.Counted)]
    [InlineData(ExpenseCategory.Fuel, false, false, false, ExpenseVehicleCostRule.NoVehicle)]
    [InlineData(ExpenseCategory.Maintenance, true, true, false, ExpenseVehicleCostRule.CountedByMaintenance)]
    [InlineData(ExpenseCategory.Repairs, true, false, true, ExpenseVehicleCostRule.CountedByClaim)]
    [InlineData(ExpenseCategory.Insurance, true, false, false, ExpenseVehicleCostRule.CountedByVehicleDocument)]
    [InlineData(ExpenseCategory.RoadTax, true, false, false, ExpenseVehicleCostRule.CountedByVehicleDocument)]
    [InlineData(ExpenseCategory.Leasing, true, false, false, ExpenseVehicleCostRule.CountedByDepreciation)]
    [InlineData(ExpenseCategory.CommissionBookingCom, false, false, false, ExpenseVehicleCostRule.NoVehicle)]
    public void An_expense_counts_once_in_the_costs_of_its_vehicle(ExpenseCategory category, bool vehicle, bool maintenance, bool claim, ExpenseVehicleCostRule rule)
    {
        ExpenseVehicleCosts.RuleOf(
            category,
            vehicle ? Guid.CreateVersion7() : null,
            maintenance ? Guid.CreateVersion7() : null,
            claim ? Guid.CreateVersion7() : null).ShouldBe(rule);
    }

    [Fact]
    public void A_cash_expense_is_an_outflow_of_the_revenue_cash_and_its_reversal_puts_it_back()
    {
        var session = CashSession.Open(AgencyId, Accountant.UserId!.Value, "Comptable", Money.Tnd(100m), At).Value;
        var expenseId = Guid.CreateVersion7();

        session.RecordExpensePaid(expenseId, Money.Tnd(150m), "DEP-2027-000001 Agil", Accountant, At).Error.ShouldBe(CashErrors.InsufficientCash);
        var paid = session.RecordExpensePaid(expenseId, Money.Tnd(60m), "DEP-2027-000001 Agil", Accountant, At).Value;

        paid.Kind.ShouldBe(CashMovementKind.ExpensePaid);
        paid.ExpenseId.ShouldBe(expenseId);
        paid.RevenueEffect.ShouldBe(Money.Tnd(-60m));
        paid.Reason.ShouldBe("DEP-2027-000001 Agil");
        session.ExpectedRevenueCash.ShouldBe(Money.Tnd(40m));
        session.RecordExpensePaid(expenseId, Money.Tnd(1m), "x", Accountant, At).Error.ShouldBe(CashErrors.DuplicateMovement);

        var other = Guid.CreateVersion7();
        var reversed = session.RecordExpenseReversed(expenseId, Money.Tnd(60m), "DEP-2027-000001 Agil", other, Accountant, At).Value;
        reversed.Kind.ShouldBe(CashMovementKind.ExpenseReversed);
        reversed.RelatedSessionId.ShouldBe(other);
        session.ExpectedRevenueCash.ShouldBe(Money.Tnd(100m));
        session.RecordExpenseReversed(expenseId, Money.Tnd(60m), "x", null, Accountant, At).Error.ShouldBe(CashErrors.DuplicateMovement);
    }

    private static Result<Expense> Record(ExpenseDetails details) => Expense.Record(details, Number, null, Today, Accountant, At);

    private static ExpenseDetails Details(string supplier = "Station Agil", string? reference = null) => new(
        AgencyId,
        Today,
        ExpenseCategory.Fuel,
        supplier,
        ExpenseAmounts.FromInclVat(Money.Tnd(119m), 0.19m).Value,
        ExpensePaymentMethod.BankTransfer,
        Reference: reference);
}
