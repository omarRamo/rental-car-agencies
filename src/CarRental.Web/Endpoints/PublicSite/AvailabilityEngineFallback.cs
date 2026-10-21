using CarRental.Application.Bookings.Availability;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>
/// TODO(1E): remove once the Bookings package is merged. The availability engine (ADR-0025) is registered by the
/// Bookings module together with its <see cref="IOccupancyReader"/>; until that module exists, no booking can occupy a
/// vehicle, so the public site registers the same engine over an empty occupancy. When the Bookings module registers
/// its reader, this does nothing.
/// </summary>
internal static class AvailabilityEngineFallback
{
    public static IServiceCollection AddAvailabilityEngineUntilBookingsExist(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(IOccupancyReader)))
        {
            return services;
        }

        services.AddScoped<IOccupancyReader, NoBookingsOccupancyReader>();
        services.AddBookingAvailability();
        return services;
    }

    /// <summary>There is no booking yet: nothing occupies the fleet.</summary>
    private sealed class NoBookingsOccupancyReader : IOccupancyReader
    {
        public Task<IReadOnlyList<OccupancyInterval>> GetOccupancyAsync(
            Guid categoryId,
            Period range,
            Guid? excludedBookingId,
            OccupancyBasis basis,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OccupancyInterval>>([]);
    }
}
