using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Bookings;

/// <summary>Bookings module: rental files and their status machine, frozen quotes, availability engine, hand-overs and returns. Built in Phase 1; registration is empty until then.</summary>
public static class BookingsModule
{
    public static IServiceCollection AddBookingsModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
