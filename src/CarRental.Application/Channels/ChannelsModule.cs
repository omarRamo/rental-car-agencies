using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Channels;

/// <summary>Channels module: availability, rates, quotas and stop-sales per channel, Booking.com adapter. Built in Phase 5; registration is empty until then.</summary>
public static class ChannelsModule
{
    public static IServiceCollection AddChannelsModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
