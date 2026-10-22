using CarRental.Application.Billing.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Cash.Expenses;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Accounting.Fakes;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Expenses;

/// <summary>
/// The <c>expenses</c> source of the costs of the vehicles (ADR-0046, ADR-0063) never counts a cost twice: it counts the
/// fuel, maintenance, repairs and other expenses of a vehicle excluding VAT on their date, never an expense that pays a
/// maintenance (the <c>fleet</c> source counts it), a claim (the <c>claims</c> source), insurance or road tax (prices of the
/// documents of the vehicle) or a leasing rent (in the depreciation), nor a deleted one.
/// </summary>
public sealed class ExpensesVehicleCostsSourceTests
{
    private static readonly DateOnly March = new(2027, 3, 1);

    private readonly AccountingHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_the_costs_no_other_source_counts_are_counted_once_excluding_vat_on_their_date()
    {
        var expenses = _harness.Expenses(AccountingHarness.User(Roles.Accountant));
        var agencyId = _harness.AgencyId;
        var vehicle = _harness.Fleet.AddVehicle(agencyId, "245 TU 1234");
        var maintenance = new MaintenanceRecordInfo(Guid.CreateVersion7(), vehicle.Id, MaintenanceTypeInfo.Brakes, MaintenanceStatusInfo.Completed, "Garage", null, AccountingHarness.Now.AddDays(-30), AccountingHarness.Now.AddDays(-29), Money.Tnd(400m));
        _harness.Fleet.Maintenances.Add(maintenance);
        var claim = new ClaimSummaryInfo(Guid.CreateVersion7(), agencyId, vehicle.Id, null, AccountingHarness.Now.AddDays(-25), null, null, ClaimStatusInfo.Open);
        _harness.Billing.Claims.Add(claim);
        async Task<Guid> Record(ExpenseCategoryInfo category, decimal inclVat, int day, Guid? vehicleId, Guid? maintenanceId = null, Guid? claimId = null) =>
            (await expenses.RecordAsync(
                new ExpenseForm(agencyId, new DateOnly(2027, 3, day), category, $"Fournisseur {day}", ExpenseAmountMode.InclVatAndRate, inclVat, null, null, 0.19m, ExpensePaymentMethodInfo.Card, VehicleId: vehicleId, MaintenanceEventId: maintenanceId, ClaimId: claimId),
                Ct)).Value;

        var fuel = await Record(ExpenseCategoryInfo.Fuel, 119m, 3, vehicle.Id);
        var other = await Record(ExpenseCategoryInfo.Other, 59.5m, 4, vehicle.Id);
        await Record(ExpenseCategoryInfo.Maintenance, 476m, 5, vehicle.Id, maintenanceId: maintenance.Id);
        await Record(ExpenseCategoryInfo.Repairs, 238m, 6, vehicle.Id, claimId: claim.Id);
        await Record(ExpenseCategoryInfo.Insurance, 1190m, 7, vehicle.Id);
        await Record(ExpenseCategoryInfo.RoadTax, 300m, 8, vehicle.Id);
        await Record(ExpenseCategoryInfo.Leasing, 1785m, 9, vehicle.Id);
        await Record(ExpenseCategoryInfo.Fuel, 50m, 10, null);
        var deleted = await Record(ExpenseCategoryInfo.Repairs, 119m, 11, vehicle.Id);
        (await expenses.DeleteAsync(deleted, "Saisie en double", Ct)).IsSuccess.ShouldBeTrue();
        await Record(ExpenseCategoryInfo.Fuel, 119m, 1, vehicle.Id); // before the period asked for

        var costs = await new ExpensesVehicleCostsSource(_harness.Db).GetCostsAsync(agencyId, new DateOnly(2027, 3, 2), March.AddMonths(1).AddDays(-1), Ct);

        costs.Select(c => (c.VehicleId, c.Amount.Amount, c.From, c.To)).ShouldBe(
        [
            (vehicle.Id, 100m, new DateOnly(2027, 3, 3), new DateOnly(2027, 3, 3)),
            (vehicle.Id, 50m, new DateOnly(2027, 3, 4), new DateOnly(2027, 3, 4)),
        ]);
        costs[0].Label.ShouldBe($"{Number(fuel)} Fournisseur 3");
        costs[1].Label.ShouldBe($"{Number(other)} Fournisseur 4");
        (await new ExpensesVehicleCostsSource(_harness.Db).GetCostsAsync(Guid.CreateVersion7(), March, March.AddMonths(1), Ct)).ShouldBeEmpty("another agency");
        (await new ExpensesVehicleCostsSource(_harness.Db).GetCostsAsync(agencyId, March.AddMonths(1), March, Ct)).ShouldBeEmpty("reversed dates");
        new ExpensesVehicleCostsSource(_harness.Db).Name.ShouldBe("expenses");
    }

    private string Number(Guid expenseId) => _harness.Db.Rows<CarRental.Domain.Cash.Expenses.Expense>().Single(e => e.Id == expenseId).Number;
}
