using CarRental.Application.Bookings.Jobs;

namespace CarRental.Web.Components.Admin.Bookings;

/// <summary>Web services of the Bookings module: the localized escalation e-mail of the managers.</summary>
internal static class BookingsWeb
{
    public static IServiceCollection AddBookingsWeb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IBookingEscalationEmailFactory, BookingEscalationEmailFactory>();
        return services;
    }
}
