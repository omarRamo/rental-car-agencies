using System.Security.Cryptography;
using CarRental.Application.Accounting.Periods;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Cash.Expenses;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Accounting.Fakes;
using CarRental.Domain.Accounting;
using CarRental.Domain.Cash;
using CarRental.Domain.Cash.Expenses;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Expenses;

/// <summary>
/// The expenses of the back office (ADR-0063): numbered per agency and year, amounts computed like the pricing engine,
/// paid in cash through the open session of the user, linked to a vehicle, a maintenance or a claim of the agency, the
/// receipt checked and stored privately, changed and deleted (logically, with a reason) until their month is closed.
/// </summary>
public sealed class ExpenseServiceTests
{
    private static readonly DateOnly MarchTenth = new(2027, 3, 10);
    private static readonly DateOnly Today = new(2027, 4, 2);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private readonly AccountingHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid AgencyId => _harness.AgencyId;

    [Fact]
    public async Task An_expense_is_numbered_per_agency_and_year_with_its_amounts_rounded_like_the_pricing_engine()
    {
        var expenses = _harness.Expenses(Accountant());

        var first = (await expenses.RecordAsync(BankForm(119m), Ct)).Value;
        var second = (await expenses.RecordAsync(BankForm(10m) with { Date = new DateOnly(2027, 1, 5) }, Ct)).Value;
        var invoiced = (await expenses.RecordAsync(BankForm(0m) with { AmountMode = ExpenseAmountMode.AsInvoiced, InclVat = null, ExclVat = 50m, Vat = 9.5m, VatRate = null }, Ct)).Value;
        var lastYear = (await expenses.RecordAsync(BankForm(20m) with { Date = new DateOnly(2026, 12, 31) }, Ct)).Value;

        (Stored(first).Number, Stored(first).ExclVat, Stored(first).Vat, Stored(first).InclVat).ShouldBe(("DEP-2027-000001", Tnd(100m), Tnd(19m), Tnd(119m)));
        (Stored(second).Number, Stored(second).ExclVat, Stored(second).Vat).ShouldBe(("DEP-2027-000002", Tnd(8.403m), Tnd(1.597m)));
        (Stored(invoiced).Number, Stored(invoiced).InclVat, Stored(invoiced).VatRate).ShouldBe(("DEP-2027-000003", Tnd(59.5m), (decimal?)0.19m), "the rate of an invoice is read from its amounts");
        Stored(lastYear).Number.ShouldBe("DEP-2026-000001");
        Stored(first).PaymentReference.ShouldBe("VIR-1");
        Stored(first).CashSessionId.ShouldBeNull();
        _harness.Db.Rows<ExpenseSequence>().Count.ShouldBe(2);
        _harness.Lock.Acquired.ShouldContain((AccountingLocks.Period(AgencyId, AccountingMonth.Of(MarchTenth)), false));
    }

    [Fact]
    public async Task The_amounts_the_date_and_the_texts_of_an_expense_are_checked()
    {
        var expenses = _harness.Expenses(Accountant());

        (await expenses.RecordAsync(BankForm(119m) with { Date = Today.AddDays(1) }, Ct)).Error.ShouldBe(ExpenseErrors.DateInvalid);
        (await expenses.RecordAsync(BankForm(1.2345m), Ct)).Error.ShouldBe(ExpenseErrors.AmountInvalid);
        (await expenses.RecordAsync(BankForm(119m) with { VatRate = 1.5m }, Ct)).Error.ShouldBe(ExpenseErrors.VatRateInvalid);
        (await expenses.RecordAsync(BankForm(119m) with { Supplier = " x " }, Ct)).Error.ShouldBe(ExpenseErrors.SupplierInvalid);
        (await expenses.RecordAsync(BankForm(119m) with { PaymentMethod = ExpensePaymentMethodInfo.Cheque, PaymentReference = null }, Ct)).Error.ShouldBe(ExpenseErrors.ChequeNumberRequired);
        (await expenses.RecordAsync(BankForm(119m) with { AgencyId = Guid.CreateVersion7() }, Ct)).Error.ShouldBe(ExpenseErrors.AgencyNotFound);
        (await expenses.RecordAsync(BankForm(119m) with { AmountMode = (ExpenseAmountMode)42 }, Ct)).Error.ShouldBe(ExpenseErrors.AmountInvalid);
        _harness.Db.Rows<Expense>().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_expense_paid_in_cash_is_an_outflow_of_the_open_session_of_the_user_in_the_same_unit_of_work()
    {
        var accountant = Accountant();
        var sessionId = await _harness.OpenSessionAsync(accountant, 200m);

        var id = (await _harness.Expenses(accountant).RecordAsync(CashForm(59.5m), Ct)).Value;

        Stored(id).CashSessionId.ShouldBe(sessionId);
        var session = _harness.Session(sessionId);
        var movement = session.Movements.ShouldHaveSingleItem();
        movement.Kind.ShouldBe(CashMovementKind.ExpensePaid);
        movement.ExpenseId.ShouldBe(id);
        movement.Amount.ShouldBe(Tnd(59.5m));
        session.ExpectedRevenueCash.ShouldBe(Tnd(140.5m));
    }

    [Fact]
    public async Task A_cash_expense_needs_the_cash_permission_and_an_open_session_of_the_user_holding_the_cash()
    {
        var accountant = Accountant();

        (await _harness.Expenses(UserWith(Permissions.ExpensesManage)).RecordAsync(CashForm(10m), Ct)).Error.ShouldBe(CashErrors.Forbidden);
        (await _harness.Expenses(accountant).RecordAsync(CashForm(10m), Ct)).Error.ShouldBe(CashErrors.NoOpenSession);
        await _harness.OpenSessionAsync(accountant, 20m);
        (await _harness.Expenses(accountant).RecordAsync(CashForm(59.5m), Ct)).Error.ShouldBe(CashErrors.InsufficientCash);
        _harness.Db.Rows<Expense>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bank_expense_is_changed_in_its_current_version_and_never_becomes_a_cash_one()
    {
        var expenses = _harness.Expenses(Accountant());
        var id = (await expenses.RecordAsync(BankForm(119m), Ct)).Value;

        (await expenses.ChangeAsync(id, 1, BankForm(238m) with { Supplier = "Total Énergies", Reference = "F-77" }, Ct)).IsSuccess.ShouldBeTrue();

        var expense = Stored(id);
        (expense.Supplier, expense.Reference, expense.InclVat, expense.Revision).ShouldBe(("Total Énergies", "F-77", Tnd(238m), 2));
        (await expenses.ChangeAsync(id, 1, BankForm(119m), Ct)).Error.ShouldBe(ExpenseErrors.Stale);
        (await expenses.ChangeAsync(id, 2, CashForm(238m) with { Date = MarchTenth }, Ct)).Error.ShouldBe(ExpenseErrors.PaymentMethodChangeNotAllowed);
        (await expenses.ChangeAsync(Guid.CreateVersion7(), 1, BankForm(119m), Ct)).Error.ShouldBe(ExpenseErrors.NotFound);
    }

    [Fact]
    public async Task A_cash_expense_keeps_its_date_amounts_and_means_of_payment()
    {
        var accountant = Accountant();
        await _harness.OpenSessionAsync(accountant, 200m);
        var expenses = _harness.Expenses(accountant);
        var id = (await expenses.RecordAsync(CashForm(59.5m), Ct)).Value;

        (await expenses.ChangeAsync(id, 1, CashForm(60m), Ct)).Error.ShouldBe(ExpenseErrors.CashAmountsLocked);
        (await expenses.ChangeAsync(id, 1, CashForm(59.5m) with { Date = Today.AddDays(-1) }, Ct)).Error.ShouldBe(ExpenseErrors.CashAmountsLocked);
        (await expenses.ChangeAsync(id, 1, CashForm(59.5m) with { PaymentMethod = ExpensePaymentMethodInfo.Card }, Ct)).Error.ShouldBe(ExpenseErrors.CashAmountsLocked);
        (await expenses.ChangeAsync(id, 1, CashForm(59.5m) with { Supplier = "Shell Lac 2", Description = "Plein du véhicule de courtoisie" }, Ct)).IsSuccess.ShouldBeTrue();
        Stored(id).Supplier.ShouldBe("Shell Lac 2");
    }

    [Fact]
    public async Task Deleting_a_cash_expense_needs_a_reason_and_puts_the_cash_back_in_the_open_session_of_the_user()
    {
        var accountant = Accountant();
        var sessionId = await _harness.OpenSessionAsync(accountant, 200m);
        var expenses = _harness.Expenses(accountant);
        var cash = (await expenses.RecordAsync(CashForm(59.5m), Ct)).Value;
        var bank = (await expenses.RecordAsync(BankForm(119m), Ct)).Value;

        (await expenses.DeleteAsync(cash, "non", Ct)).Error.ShouldBe(ExpenseErrors.DeletionReasonRequired);
        (await expenses.DeleteAsync(cash, "Saisie en double", Ct)).IsSuccess.ShouldBeTrue();

        var deleted = Stored(cash);
        (deleted.IsDeleted, deleted.DeletionReason, deleted.ReversedInSessionId).ShouldBe((true, "Saisie en double", (Guid?)sessionId));
        var session = _harness.Session(sessionId);
        session.Movements.Select(m => m.Kind).ShouldBe([CashMovementKind.ExpensePaid, CashMovementKind.ExpenseReversed]);
        session.ExpectedRevenueCash.ShouldBe(Tnd(200m));
        (await expenses.DeleteAsync(cash, "Saisie en double", Ct)).Error.ShouldBe(ExpenseErrors.Deleted);
        (await expenses.ChangeAsync(cash, deleted.Revision, CashForm(59.5m), Ct)).Error.ShouldBe(ExpenseErrors.Deleted);

        var list = (await expenses.ListAsync(new ExpenseFilter(AgencyId), new PageRequest(1, 25), Ct)).Value;
        list.Page.Items.ShouldHaveSingleItem().Id.ShouldBe(bank);
        list.TotalInclVat.ShouldBe(Tnd(119m));
        var all = (await expenses.ListAsync(new ExpenseFilter(AgencyId, IncludeDeleted: true), new PageRequest(1, 25), Ct)).Value;
        all.Page.Items.Select(e => e.Id).ShouldBe([cash, bank], ignoreOrder: true);
        all.Count.ShouldBe(1, "a deleted expense never counts in the totals");
        all.TotalInclVat.ShouldBe(Tnd(119m));
        var view = (await expenses.GetAsync(cash, Ct)).Value;
        (view.IsDeleted, view.IsEditable, view.DeletedByName).ShouldBe((true, false, accountant.UserName));
    }

    [Fact]
    public async Task Nothing_is_recorded_changed_or_deleted_in_a_closed_month_but_a_receipt_still_arrives()
    {
        var expenses = _harness.Expenses(Accountant());
        var march = (await expenses.RecordAsync(BankForm(119m), Ct)).Value;
        var april = (await expenses.RecordAsync(BankForm(50m) with { Date = Today }, Ct)).Value;
        _harness.CloseMonth(AccountingMonth.Of(MarchTenth));

        (await expenses.RecordAsync(BankForm(10m) with { Date = new DateOnly(2027, 3, 31) }, Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await expenses.ChangeAsync(march, 1, BankForm(120m), Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await expenses.ChangeAsync(april, 1, BankForm(50m) with { Date = MarchTenth }, Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await expenses.DeleteAsync(march, "Saisie en double", Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await expenses.UploadReceiptAsync(march, "ticket.png", new MemoryStream(Png), Ct)).IsSuccess.ShouldBeTrue();

        var view = (await expenses.GetAsync(march, Ct)).Value;
        (view.IsInClosedMonth, view.IsEditable, view.HasReceipt).ShouldBe((true, false, true));
        (await expenses.GetAsync(april, Ct)).Value.IsEditable.ShouldBeTrue();
        Stored(march).InclVat.ShouldBe(Tnd(119m));
    }

    [Fact]
    public async Task A_vehicle_a_maintenance_or_a_claim_named_by_an_expense_belongs_to_the_agency()
    {
        var expenses = _harness.Expenses(Accountant());
        var vehicle = _harness.Fleet.AddVehicle(AgencyId, "245 TU 1234");
        var foreign = _harness.Fleet.AddVehicle(Guid.CreateVersion7(), "100 TU 1");
        var maintenance = Maintenance(vehicle.Id);
        var other = Maintenance(foreign.Id);
        _harness.Fleet.Maintenances.AddRange([maintenance, other]);
        var claim = new ClaimSummaryInfo(Guid.CreateVersion7(), AgencyId, vehicle.Id, "LOC-2027-000003", AccountingHarness.Now.AddDays(-20), "STAR", "S-77", ClaimStatusInfo.AtInsurer);
        _harness.Billing.Claims.Add(claim);

        (await expenses.RecordAsync(BankForm(119m, vehicleId: foreign.Id), Ct)).Error.ShouldBe(ExpenseErrors.VehicleNotFound);
        (await expenses.RecordAsync(BankForm(119m, ExpenseCategoryInfo.PremisesRent, vehicle.Id), Ct)).Error.ShouldBe(ExpenseErrors.VehicleNotAllowed);
        (await expenses.RecordAsync(BankForm(119m, ExpenseCategoryInfo.Maintenance, vehicle.Id) with { MaintenanceEventId = other.Id }, Ct)).Error.ShouldBe(ExpenseErrors.MaintenanceNotFound);
        (await expenses.RecordAsync(BankForm(119m, ExpenseCategoryInfo.Repairs, vehicle.Id) with { ClaimId = Guid.CreateVersion7() }, Ct)).Error.ShouldBe(ExpenseErrors.ClaimNotFound);
        (await expenses.RecordAsync(BankForm(119m, ExpenseCategoryInfo.Repairs) with { ClaimId = claim.Id }, Ct)).Error.ShouldBe(ExpenseErrors.LinkInvalid);
        (await expenses.RecordAsync(BankForm(119m, ExpenseCategoryInfo.Repairs, vehicle.Id) with { ClaimId = claim.Id, MaintenanceEventId = maintenance.Id }, Ct)).Error.ShouldBe(ExpenseErrors.LinkInvalid);

        var paysMaintenance = (await expenses.RecordAsync(BankForm(119m, ExpenseCategoryInfo.Maintenance, vehicle.Id) with { MaintenanceEventId = maintenance.Id }, Ct)).Value;
        var paysClaim = (await expenses.RecordAsync(BankForm(119m, ExpenseCategoryInfo.Repairs, vehicle.Id) with { ClaimId = claim.Id }, Ct)).Value;
        var fuel = (await expenses.RecordAsync(BankForm(119m, ExpenseCategoryInfo.Fuel, vehicle.Id), Ct)).Value;

        (await expenses.GetAsync(paysMaintenance, Ct)).Value.VehicleCostRule.ShouldBe(ExpenseVehicleCostRuleInfo.CountedByMaintenance);
        (await expenses.GetAsync(paysClaim, Ct)).Value.VehicleCostRule.ShouldBe(ExpenseVehicleCostRuleInfo.CountedByClaim);
        (await expenses.GetAsync(fuel, Ct)).Value.VehicleCostRule.ShouldBe(ExpenseVehicleCostRuleInfo.Counted);
        var links = (await expenses.GetLinksAsync(AgencyId, vehicle.Id, Ct)).Value;
        links.Maintenances.ShouldHaveSingleItem().Id.ShouldBe(maintenance.Id);
        links.Claims.ShouldHaveSingleItem().Id.ShouldBe(claim.Id);
        (await expenses.GetLinksAsync(AgencyId, foreign.Id, Ct)).Error.ShouldBe(ExpenseErrors.VehicleNotFound);
        var ofVehicle = (await expenses.ListAsync(new ExpenseFilter(AgencyId, VehicleId: vehicle.Id, Category: ExpenseCategoryInfo.Repairs), new PageRequest(1, 25), Ct)).Value;
        ofVehicle.Page.Items.ShouldHaveSingleItem().Id.ShouldBe(paysClaim);
    }

    [Fact]
    public async Task A_receipt_is_checked_by_its_real_type_and_stored_privately_at_a_path_of_its_own()
    {
        var expenses = _harness.Expenses(Accountant());
        var id = (await expenses.RecordAsync(BankForm(119m), Ct)).Value;

        (await expenses.UploadReceiptAsync(id, "ticket.png", new MemoryStream(Png), Ct)).IsSuccess.ShouldBeTrue();
        (await expenses.UploadReceiptAsync(id, "ticket.png", new MemoryStream("%PDF-1.7 fake"u8.ToArray()), Ct)).IsSuccess.ShouldBeTrue();

        var expense = Stored(id);
        expense.ReceiptContentType.ShouldBe("application/pdf");
        expense.ReceiptPath.ShouldStartWith($"expenses/2027/{id:N}/");
        expense.ReceiptPath.ShouldEndWith(".pdf");
        expense.ReceiptHash.ShouldBe(Convert.ToHexStringLower(SHA256.HashData("%PDF-1.7 fake"u8)));
        _harness.Storage.Files.Keys.ShouldAllBe(k => k.Container == FileContainers.Invoices);
        _harness.Storage.Files.Count.ShouldBe(2, "a replaced receipt stays in the container");
        var file = (await expenses.FindReceiptAsync(id, Ct)).Value;
        (file.Container, file.Path, file.ContentType, file.FileName).ShouldBe((FileContainers.Invoices, expense.ReceiptPath!, "application/pdf", "ticket.png"));
        file.ETag.ShouldBe(expense.ReceiptHash![..16]);
        await using var download = (await expenses.OpenReceiptAsync(file, Ct))!.Content;
        using var copy = new MemoryStream();
        await download.CopyToAsync(copy, Ct);
        copy.ToArray().ShouldBe("%PDF-1.7 fake"u8.ToArray());

        (await expenses.UploadReceiptAsync(id, "notes.txt", new MemoryStream("plain text"u8.ToArray()), Ct)).Error.ShouldBe(ExpenseErrors.ReceiptInvalid);
        (await expenses.UploadReceiptAsync(id, "huge.png", new MemoryStream([.. Png, .. new byte[ExpenseService.MaxReceiptBytes]]), Ct)).Error.ShouldBe(ExpenseErrors.ReceiptInvalid);
        (await expenses.UploadReceiptAsync(Guid.CreateVersion7(), "ticket.png", new MemoryStream(Png), Ct)).Error.ShouldBe(ExpenseErrors.NotFound);
        (await expenses.FindReceiptAsync((await expenses.RecordAsync(BankForm(10m), Ct)).Value, Ct)).Error.ShouldBe(ExpenseErrors.ReceiptNotFound);
    }

    [Fact]
    public async Task The_list_filters_by_dates_category_means_and_text_with_the_totals_of_the_whole_filter()
    {
        var expenses = _harness.Expenses(Accountant());
        await expenses.RecordAsync(BankForm(119m), Ct);
        await expenses.RecordAsync(BankForm(238m, ExpenseCategoryInfo.Utilities) with { Supplier = "STEG", Date = new DateOnly(2027, 3, 20) }, Ct);
        await expenses.RecordAsync(BankForm(59.5m) with { Date = new DateOnly(2027, 2, 27), PaymentMethod = ExpensePaymentMethodInfo.Card }, Ct);

        var march = (await expenses.ListAsync(new ExpenseFilter(AgencyId, new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 31)), new PageRequest(1, 1), Ct)).Value;
        var steg = (await expenses.ListAsync(new ExpenseFilter(AgencyId, Search: "STEG"), new PageRequest(1, 25), Ct)).Value;
        var card = (await expenses.ListAsync(new ExpenseFilter(AgencyId, PaymentMethod: ExpensePaymentMethodInfo.Card), new PageRequest(1, 25), Ct)).Value;

        march.Page.TotalCount.ShouldBe(2);
        march.Page.Items.ShouldHaveSingleItem().Supplier.ShouldBe("STEG", "the latest first");
        (march.Count, march.TotalExclVat, march.TotalVat, march.TotalInclVat).ShouldBe((2, Tnd(300m), Tnd(57m), Tnd(357m)));
        steg.Page.Items.ShouldHaveSingleItem().Category.ShouldBe(ExpenseCategoryInfo.Utilities);
        card.Page.Items.ShouldHaveSingleItem().Date.ShouldBe(new DateOnly(2027, 2, 27));
        (await expenses.ListAsync(new ExpenseFilter(Guid.CreateVersion7()), new PageRequest(1, 25), Ct)).Value.Page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_expenses_permission_reads_or_writes_the_expenses()
    {
        var expenses = _harness.Expenses(AccountingHarness.User(Roles.Agent));
        var id = (await _harness.Expenses(Accountant()).RecordAsync(BankForm(119m), Ct)).Value;

        (await expenses.GetAgenciesAsync(Ct)).Error.ShouldBe(ExpenseErrors.Forbidden);
        (await expenses.ListAsync(new ExpenseFilter(AgencyId), new PageRequest(1, 25), Ct)).Error.ShouldBe(ExpenseErrors.Forbidden);
        (await expenses.GetAsync(id, Ct)).Error.ShouldBe(ExpenseErrors.Forbidden);
        (await expenses.GetVehiclesAsync(AgencyId, Ct)).Error.ShouldBe(ExpenseErrors.Forbidden);
        (await expenses.GetLinksAsync(AgencyId, Guid.CreateVersion7(), Ct)).Error.ShouldBe(ExpenseErrors.Forbidden);
        (await expenses.RecordAsync(BankForm(119m), Ct)).Error.ShouldBe(ExpenseErrors.Forbidden);
        (await expenses.ChangeAsync(id, 1, BankForm(120m), Ct)).Error.ShouldBe(ExpenseErrors.Forbidden);
        (await expenses.DeleteAsync(id, "Saisie en double", Ct)).Error.ShouldBe(ExpenseErrors.Forbidden);
        (await expenses.UploadReceiptAsync(id, "ticket.png", new MemoryStream(Png), Ct)).Error.ShouldBe(ExpenseErrors.Forbidden);
        (await expenses.FindReceiptAsync(id, Ct)).Error.ShouldBe(ExpenseErrors.Forbidden);
        (await _harness.Expenses(Accountant()).GetAgenciesAsync(Ct)).Value.ShouldHaveSingleItem().Id.ShouldBe(AgencyId);
    }

    private static ScopedUserContext Accountant() => AccountingHarness.User(Roles.Accountant);

    private static ScopedUserContext UserWith(params string[] permissions)
    {
        var context = new ScopedUserContext([]);
        context.Initialize(UserContextSnapshot.ForUser(Guid.CreateVersion7(), "custom@tests.local", permissions));
        return context;
    }

    private static Money Tnd(decimal amount) => Money.Tnd(amount);

    private static MaintenanceRecordInfo Maintenance(Guid vehicleId) =>
        new(Guid.CreateVersion7(), vehicleId, MaintenanceTypeInfo.OilChange, MaintenanceStatusInfo.Completed, "Garage Ennasr", null, AccountingHarness.Now.AddDays(-10), AccountingHarness.Now.AddDays(-9), Tnd(150m));

    private Expense Stored(Guid id) => _harness.Db.Rows<Expense>().Single(e => e.Id == id);

    private ExpenseForm BankForm(decimal inclVat, ExpenseCategoryInfo category = ExpenseCategoryInfo.Fuel, Guid? vehicleId = null) =>
        new(AgencyId, MarchTenth, category, "Station Agil", ExpenseAmountMode.InclVatAndRate, inclVat, null, null, 0.19m, ExpensePaymentMethodInfo.BankTransfer, "VIR-1", VehicleId: vehicleId);

    private ExpenseForm CashForm(decimal inclVat) =>
        new(AgencyId, Today, ExpenseCategoryInfo.Fuel, "Station Agil", ExpenseAmountMode.InclVatAndRate, inclVat, null, null, 0.19m, ExpensePaymentMethodInfo.Cash);
}
