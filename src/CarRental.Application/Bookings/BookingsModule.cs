using CarRental.Application.Bookings.Administration;
using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Handover;
using CarRental.Application.Bookings.Jobs;
using CarRental.Application.Bookings.Notifications;
using CarRental.Application.Bookings.Parties;
using CarRental.Application.Bookings.Payments;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Application.Bookings.Queries;
using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Bookings.Seeding;
using CarRental.Application.Bookings.Writing;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Security;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Bookings;

/// <summary>
/// Bookings module: rental files and their status machine, frozen quotes and their versions, the availability engine
/// (cache, guard, occupancy), quick creation, request validation, amendments, vehicle assignment and planning, the
/// expiry and escalation jobs, and the hand-over transitions used by the departure and return package (ADR-0030 to 0033).
/// </summary>
public static class BookingsModule
{
    public static IServiceCollection AddBookingsModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Availability engine (ADR-0025): cache (the application's IAvailabilityChangeNotifier), guard, queries.
        services.AddBookingAvailability();
        services.AddScoped<IOccupancyReader, OccupancyReader>();

        // Contracts for the other modules and the public funnel.
        services.AddScoped<IBookingsQueries, BookingsQueries>();
        services.AddScoped<IBookingsReportingQueries, BookingsReportingQueries>();
        services.AddScoped<IBookingRequests, BookingRequestService>();
        services.AddScoped<IBookingPaymentStatus, BookingPaymentStatus>();
        services.AddScoped<IChannelBookings, ChannelBookingService>();

        // Internals, one instance per operation scope.
        services.AddScoped<BookingWriter>();
        services.AddScoped<QuoteStore>();
        services.AddScoped<PartyDirectory>();
        services.AddScoped<DriverChecks>();
        services.AddScoped<BookingPricing>();

        // Back office services (their constructors are internal: built here).
        services.AddScoped(sp => new QuickBookingService(
            sp.GetRequiredService<BookingWriter>(),
            sp.GetRequiredService<BookingPricing>(),
            sp.GetRequiredService<QuoteStore>(),
            sp.GetRequiredService<DriverChecks>(),
            sp.GetRequiredService<ICustomerRegistration>(),
            sp.GetRequiredService<ISettingsReader>()));
        services.AddScoped(sp => new BookingWorkflowService(
            sp.GetRequiredService<BookingWriter>(),
            sp.GetRequiredService<DriverChecks>(),
            sp.GetRequiredService<ISettingsReader>(),
            sp.GetService<IOnlinePayments>()));
        services.AddScoped(sp => new BookingAmendmentService(
            sp.GetRequiredService<BookingWriter>(),
            sp.GetRequiredService<BookingPricing>(),
            sp.GetRequiredService<QuoteStore>(),
            sp.GetRequiredService<ICustomerRegistration>(),
            sp.GetRequiredService<IAvailabilityQueries>()));
        services.AddScoped(sp => new VehicleAssignmentService(
            sp.GetRequiredService<BookingWriter>(),
            sp.GetRequiredService<AvailabilitySnapshotLoader>(),
            sp.GetRequiredService<IFleetQueries>()));
        services.AddScoped(sp => new RentalLifecycleService(
            sp.GetRequiredService<BookingWriter>(),
            sp.GetRequiredService<DriverChecks>(),
            sp.GetRequiredService<ICustomerRegistration>()));
        services.AddScoped(sp => new BookingAdministrationQueries(
            sp.GetRequiredService<Common.Persistence.IAppDbContext>(),
            sp.GetRequiredService<IUserContext>(),
            sp.GetRequiredService<IBookingsQueries>(),
            sp.GetRequiredService<IFleetQueries>(),
            sp.GetRequiredService<IReferentialQueries>(),
            sp.GetRequiredService<ICustomersQueries>(),
            sp.GetRequiredService<IPricingService>(),
            sp.GetRequiredService<IPricingQueries>(),
            sp.GetRequiredService<IAvailabilityQueries>(),
            sp.GetRequiredService<ISettingsReader>(),
            sp.GetRequiredService<PartyDirectory>(),
            sp.GetRequiredService<DriverChecks>(),
            sp.GetRequiredService<TimeProvider>()));

        // Departure and return (package 1F-b, ADR-0041 and ADR-0042).
        services.AddBookingHandovers();

        services.AddSettingDefinitions(BookingSettings.All);
        services.AddRecurringJob<BookingExpiryJob>();
        services.AddRecurringJob<BookingEscalationJob>();
        services.AddDataSeeder<BookingsDemoSeeder>();

        // Public booking funnel, "Ma réservation" and the booking ↔ payment wiring (phase 2C, ADR-0049 to ADR-0051).
        services.AddScoped<FunnelService>();
        services.AddScoped<MyBookingService>();
        services.AddScoped<CustomerLinkService>();
        services.AddSettingDefinitions(PublicBookingSettings.All);
        services.AddRecurringJob<PublicAccessPurgeJob>();
        services.AddOutboxHandler<BookingStatusChanged, BookingPaymentReleaseHandler>();
        services.AddOutboxHandler<BookingStatusChanged, BookingPaymentLinkHandler>();
        services.AddOutboxHandler<BookingStatusChanged, BookingNotificationPublisher>();
        services.AddOutboxHandler<OnlinePaymentStatusChanged, BookingOnlinePaymentHandler>();
        services.AddOutboxHandler<BookingCustomerNotification, BookingNotificationEmailHandler>();
        services.AddOutboxHandler<AccessEmailRequested, AccessEmailHandler>();
        return services;
    }
}
