using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Cash;

/// <summary>Cash and expenses module: cash sessions, expenses, fines and claims. Built in Phases 1 and 3; registration is empty until then.</summary>
public static class CashModule
{
    public static IServiceCollection AddCashModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
