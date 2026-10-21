using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Channels.Adapters;
using CarRental.Application.Channels.Administration;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Channels.Core;
using CarRental.Application.Channels.Inbound;
using CarRental.Application.Channels.Logging;
using CarRental.Application.Channels.Outbound;
using CarRental.Application.Channels.Queries;
using CarRental.Application.Channels.Restrictions;
using CarRental.Application.Channels.Seeding;
using CarRental.Application.Common.Availability;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Caching;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Application.Channels;

/// <summary>
/// Channels module (docs/conception/phase-5-canaux.md, ADR-0052 and ADR-0053): channels and their mappings, quotas and
/// stop-sells applied by the availability engine (<see cref="IChannelAvailabilityPolicy"/> replaced), the adapters
/// (manual, and the test adapter registered by the infrastructure in Development and Testing; Booking.com once its
/// format is known), the inbound core with its message log, the outbound queue (debounce, retries, nightly
/// resynchronization), commissions and reconciliation, and the back office services. Called after the Bookings module.
/// </summary>
public static class ChannelsModule
{
    public static IServiceCollection AddChannelsModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        services.AddOptions<ChannelSyncOptions>();
        services.TryAddSingleton(TimeProvider.System);

        // Quotas and stop-sells for every reader and writer of the availability engine (ADR-0025, ADR-0044).
        services.AddSingleton<ChannelSyncSignal>();
        DecorateAvailabilityNotifier(services);
        services.AddSingleton<ChannelRestrictionsCache>();
        services.AddSingleton<ICommittedChangesListener>(provider => provider.GetRequiredService<ChannelRestrictionsCache>());
        services.AddScoped<ChannelRestrictionReader>();
        services.Replace(ServiceDescriptor.Scoped<IChannelAvailabilityPolicy, ChannelAvailabilityPolicy>());

        // Adapters: the manual one everywhere (the test adapter and Booking.com are added by the infrastructure).
        services.AddSingleton<IChannelAdapter, ManualChannelAdapter>();
        services.AddSingleton<ChannelAdapterRegistry>();

        // Core, inbound pipeline and message log.
        services.AddScoped<ChannelContextLoader>();
        services.AddScoped(provider => new ChannelReservationCore(
            provider.GetRequiredService<IAppDbContext>(),
            provider.GetRequiredService<IChannelBookings>(),
            provider.GetRequiredService<IBookingsQueries>(),
            provider.GetRequiredService<IAvailabilityQueries>(),
            provider.GetRequiredService<IPricingService>(),
            provider.GetRequiredService<ChannelRestrictionReader>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new ChannelMessageLogger(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<ChannelMessageLogger>>()));
        services.AddSingleton(provider => new ChannelInboundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ChannelAdapterRegistry>(),
            provider.GetRequiredService<ChannelMessageLogger>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ChannelInboundService>>()));

        // Outbound synchronization.
        services.AddScoped<ChannelSyncQueue>();
        services.AddScoped<ChannelSyncPayloads>();
        services.AddScoped(provider => new ChannelSyncDispatcher(
            provider.GetRequiredService<IAppDbContext>(),
            provider.GetRequiredService<ChannelSyncSignal>(),
            provider.GetRequiredService<ChannelSyncQueue>(),
            provider.GetRequiredService<ChannelSyncPayloads>(),
            provider.GetRequiredService<ChannelContextLoader>(),
            provider.GetRequiredService<ChannelAdapterRegistry>(),
            provider.GetRequiredService<IPricingQueries>(),
            provider.GetRequiredService<ChannelMessageLogger>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<ChannelSyncOptions>>(),
            provider.GetRequiredService<ILogger<ChannelSyncDispatcher>>()));
        services.AddRecurringJob<ChannelSyncJob>();
        services.AddRecurringJob<ChannelFullResyncJob>();
        services.AddRecurringJob<ChannelMessageLogRetentionJob>();

        // Contracts for the other modules, and the mirror of the rental files.
        services.AddScoped<IChannelsQueries, ChannelsQueries>();
        services.AddScoped<IChannelCommissions, ChannelCommissions>();
        services.AddScoped<IChannelCommissionSource, ChannelCommissionReportingSource>();
        services.AddOutboxHandler<BookingStatusChanged, ChannelBookingStatusMirror>();

        // Back office services (their constructors are internal: built here).
        services.AddScoped(provider => new ChannelAdministrationService(
            provider.GetRequiredService<IAppDbContext>(),
            provider.GetRequiredService<IUnitOfWork>(),
            provider.GetRequiredService<IUserContext>(),
            provider.GetRequiredService<IReferentialQueries>(),
            provider.GetRequiredService<IFleetQueries>(),
            provider.GetRequiredService<IPricingQueries>(),
            provider.GetRequiredService<ChannelAdapterRegistry>(),
            provider.GetRequiredService<ChannelSyncQueue>(),
            provider.GetRequiredService<ChannelSyncSignal>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddScoped(provider => new ChannelRestrictionService(
            provider.GetRequiredService<IAppDbContext>(),
            provider.GetRequiredService<IUnitOfWork>(),
            provider.GetRequiredService<IUserContext>(),
            provider.GetRequiredService<IFleetQueries>(),
            provider.GetRequiredService<IReferentialQueries>(),
            provider.GetRequiredService<ChannelSyncQueue>(),
            provider.GetRequiredService<ChannelSyncSignal>(),
            provider.GetRequiredService<IAvailabilityChangeNotifier>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddScoped(provider => new ChannelManualEntryService(
            provider.GetRequiredService<IAppDbContext>(),
            provider.GetRequiredService<IUserContext>(),
            provider.GetRequiredService<ChannelContextLoader>(),
            provider.GetRequiredService<ChannelReservationCore>(),
            provider.GetRequiredService<IFleetQueries>(),
            provider.GetRequiredService<IReferentialQueries>(),
            provider.GetRequiredService<IPricingService>(),
            provider.GetRequiredService<ChannelMessageLogger>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddScoped(provider => new ChannelMonitoringService(
            provider.GetRequiredService<IAppDbContext>(),
            provider.GetRequiredService<IUserContext>(),
            provider.GetRequiredService<IFleetQueries>(),
            provider.GetRequiredService<ChannelSyncDispatcher>(),
            provider.GetRequiredService<ChannelSyncSignal>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<ChannelSyncOptions>>()));
        services.AddScoped(provider => new ChannelReconciliationService(
            provider.GetRequiredService<IAppDbContext>(),
            provider.GetRequiredService<IUserContext>(),
            provider.GetRequiredService<TimeProvider>()));

        services.AddSettingDefinitions(ChannelsSettings.All);
        services.AddDataSeeder<ChannelsDemoSeeder>();
        return services;
    }

    /// <summary>
    /// Wraps the application's availability notifier (the cache registered by the Bookings module) so that every
    /// availability change also reaches the connected channels.
    /// </summary>
    private static void DecorateAvailabilityNotifier(IServiceCollection services)
    {
        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IAvailabilityChangeNotifier));
        services.Replace(ServiceDescriptor.Singleton<IAvailabilityChangeNotifier>(provider =>
            new ChannelAwareAvailabilityNotifier(Create(provider, inner), provider.GetRequiredService<ChannelSyncSignal>())));

        static IAvailabilityChangeNotifier Create(IServiceProvider provider, ServiceDescriptor? descriptor) => descriptor switch
        {
            null => new NullAvailabilityChangeNotifier(),
            { ImplementationInstance: IAvailabilityChangeNotifier instance } => instance,
            { ImplementationFactory: { } factory } => (IAvailabilityChangeNotifier)factory(provider),
            { ImplementationType: { } type } => (IAvailabilityChangeNotifier)ActivatorUtilities.CreateInstance(provider, type),
            _ => new NullAvailabilityChangeNotifier(),
        };
    }
}
