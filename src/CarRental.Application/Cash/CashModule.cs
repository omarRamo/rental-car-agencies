using CarRental.Application.Cash.Contracts;
using CarRental.Application.Cash.Seeding;
using CarRental.Application.Common.Seeding;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Cash;

/// <summary>
/// Cash module (Phase 1: cash sessions): one session per agent, agency and day, with its opening float, typed movements,
/// expected revenue cash and deposit cash, closing count, justified variances and lock (ADR-0035). The Payments module
/// puts cash payments and deposits into the drawer through <see cref="ICashDrawer"/>. Expenses, fines and claims come in
/// Phase 3.
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

        services.AddDataSeeder<CashDemoSeeder>();
        return services;
    }
}
