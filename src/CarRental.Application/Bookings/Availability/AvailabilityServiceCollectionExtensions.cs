using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Availability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarRental.Application.Bookings.Availability;

public static class AvailabilityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the availability engine (ADR-0025): the cached <see cref="IAvailabilityQueries"/>, the writers'
    /// <see cref="IAvailabilityGuard"/>, the in-memory cache — which replaces the null
    /// <see cref="IAvailabilityChangeNotifier"/> — and, unless another one is registered, the channel policy without
    /// restriction. Called by the Bookings module, which must also register its <see cref="IOccupancyReader"/>.
    /// </summary>
    public static IServiceCollection AddBookingAvailability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLogging();
        services.AddOptions<AvailabilityCacheOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AvailabilityCache>();
        services.Replace(ServiceDescriptor.Singleton<IAvailabilityChangeNotifier>(provider => provider.GetRequiredService<AvailabilityCache>()));
        services.TryAddScoped<IChannelAvailabilityPolicy, NoChannelRestrictions>();
        services.TryAddScoped<AvailabilitySnapshotLoader>();
        services.TryAddScoped<IAvailabilityQueries, AvailabilityQueries>();
        services.TryAddScoped<IAvailabilityGuard, AvailabilityGuard>();
        return services;
    }
}
