using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Common.Seeding;

/// <summary>
/// A seed step run at startup, after migrations, in its own DI scope with the system actor (<c>system:seed</c>).
/// Steps run by <see cref="Stage"/> then <see cref="Order"/>. They must be idempotent (check before insert):
/// they run at every start.
/// </summary>
/// <remarks>
/// Order conventions: 100 identity (roles), 200 referential, 300 identity demo users, 400 fleet, 500 pricing,
/// 600 customers, 700 bookings, 800 finance. Modules register their own steps in their <c>Add{Module}Module</c>.
/// </remarks>
public interface IDataSeeder
{
    SeedStage Stage { get; }

    int Order { get; }

    Task SeedAsync(CancellationToken cancellationToken);
}

public enum SeedStage
{
    /// <summary>Reference data needed in every environment (roles and permissions, default settings).</summary>
    Reference = 0,

    /// <summary>Demonstration data, only when <c>Seed:Demo</c> is true (development and recette, never production).</summary>
    Demo = 1,
}

public static class DataSeederServiceCollectionExtensions
{
    public static IServiceCollection AddDataSeeder<TSeeder>(this IServiceCollection services)
        where TSeeder : class, IDataSeeder
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IDataSeeder, TSeeder>();
        return services;
    }
}
