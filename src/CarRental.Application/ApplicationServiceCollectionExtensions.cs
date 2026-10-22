using CarRental.Application.Accounting;
using CarRental.Application.Billing;
using CarRental.Application.Bookings;
using CarRental.Application.Cash;
using CarRental.Application.Channels;
using CarRental.Application.Common.Availability;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers;
using CarRental.Application.Fleet;
using CarRental.Application.Identity;
using CarRental.Application.Messaging;
using CarRental.Application.Payments;
using CarRental.Application.Pricing;
using CarRental.Application.Referential;
using CarRental.Application.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarRental.Application;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the application layer: cross-cutting services and every module, in one place.
    /// A new module adds its <c>Add{Module}Module()</c> call here.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ScopedUserContext>();
        services.AddScoped<IUserContext>(sp => sp.GetRequiredService<ScopedUserContext>());
        services.TryAddSingleton<IAvailabilityChangeNotifier, NullAvailabilityChangeNotifier>();
        services.AddScoped<IEmailQueue, EmailQueue>();
        services.AddOutboxHandler<EmailRequested, EmailRequestedHandler>();

        services
            .AddReferentialModule()
            .AddIdentityModule()
            .AddFleetModule()
            .AddPricingModule()
            .AddBookingsModule()
            .AddCustomersModule()
            .AddPaymentsModule()
            .AddBillingModule()
            .AddCashModule()
            .AddChannelsModule()
            .AddMessagingModule()
            .AddAccountingModule()
            .AddReportingModule();

        return services;
    }
}
