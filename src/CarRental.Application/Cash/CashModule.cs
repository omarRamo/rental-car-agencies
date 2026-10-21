using CarRental.Application.Accounting.Contracts;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Cash.Expenses;
using CarRental.Application.Cash.Seeding;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarRental.Application.Cash;

/// <summary>
/// Cash module. Phase 1: cash sessions — one per agent, agency and day, with its opening float, typed movements, expected
/// revenue cash and deposit cash, closing count, justified variances and lock (ADR-0035); the Payments module puts cash
/// payments and deposits into the drawer through <see cref="ICashDrawer"/>. Package 3B: the expenses (ADR-0063) — an
/// expense paid in cash is a movement of the drawer —, their reads for the pre-accounting
/// (<see cref="ICashAccountingQueries"/>) and the Reporting source of the direct costs of the vehicles they pay.
/// </summary>
public static class CashModule
{
    public static IServiceCollection AddCashModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Contracts for the other modules and the back office.
        services.AddScoped<ICashSessions, CashSessionService>();
        services.AddScoped<ICashDrawer, CashDrawer>();
        services.AddScoped<ICashQueries, CashQueries>();
        services.AddScoped<ICashAccountingQueries, CashAccountingQueries>();

        // Expenses (their constructor is internal: built here).
        services.AddScoped(sp => new ExpenseService(
            sp.GetRequiredService<IAppDbContext>(),
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<IUserContext>(),
            sp.GetRequiredService<IReferentialQueries>(),
            sp.GetRequiredService<IFleetQueries>(),
            sp.GetRequiredService<IFleetCostRecords>(),
            sp.GetRequiredService<IBillingAccountingQueries>(),
            sp.GetRequiredService<IAccountingPeriods>(),
            sp.GetRequiredService<IFileStorage>(),
            sp.GetRequiredService<TimeProvider>()));

        // A source of the direct costs of the vehicles among others (fleet, claims): registered before the Reporting module.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IVehicleCostsSource, ExpensesVehicleCostsSource>());

        services.AddDataSeeder<CashDemoSeeder>();
        return services;
    }
}
