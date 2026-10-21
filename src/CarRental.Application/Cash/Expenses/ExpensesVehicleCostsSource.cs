using CarRental.Application.Common.Persistence;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Cash.Expenses;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Cash.Expenses;

/// <summary>
/// The direct costs of the vehicles paid by expenses (Reporting source <c>expenses</c>, ADR-0046 and ADR-0063): each expense
/// of a vehicle that <see cref="ExpenseVehicleCosts"/> counts — fuel, maintenance, repairs, other — excluding VAT, on its
/// date. Never a cost counted elsewhere: an expense that pays a maintenance (counted by the <c>fleet</c> source on its
/// completion) or a claim (counted by the <c>claims</c> source), insurance and road tax (prices of the documents of the
/// vehicle, <c>fleet</c> source) and leasing rents (in the depreciation) are left out. Deleted expenses never count.
/// </summary>
internal sealed class ExpensesVehicleCostsSource(IAppDbContext db) : IVehicleCostsSource
{
    public const string SourceName = "expenses";

    private static readonly ExpenseCategory[] CountedCategories =
        [ExpenseCategory.Fuel, ExpenseCategory.Maintenance, ExpenseCategory.Repairs, ExpenseCategory.Other];

    public string Name => SourceName;

    public async Task<IReadOnlyList<VehicleCostInfo>> GetCostsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var expenses = await db.Set<Expense>()
            .AsNoTracking()
            .Where(e => e.AgencyId == agencyId
                        && e.Date >= from
                        && e.Date <= to
                        && e.VehicleId != null
                        && e.MaintenanceEventId == null
                        && e.ClaimId == null
                        && CountedCategories.Contains(e.Category))
            .Select(e => new { e.Id, VehicleId = e.VehicleId!.Value, e.Date, e.ExclVat, e.Number, e.Supplier, e.Category, e.MaintenanceEventId, e.ClaimId })
            .ToListAsync(cancellationToken);
        return
        [
            .. expenses
                .Where(e => ExpenseVehicleCosts.RuleOf(e.Category, e.VehicleId, e.MaintenanceEventId, e.ClaimId) == ExpenseVehicleCostRule.Counted && e.ExclVat.IsPositive)
                .OrderBy(e => e.Date)
                .ThenBy(e => e.Number, StringComparer.Ordinal)
                .Select(e => new VehicleCostInfo(e.VehicleId, e.ExclVat, e.Date, e.Date, $"{e.Number} {e.Supplier}")),
        ];
    }
}
