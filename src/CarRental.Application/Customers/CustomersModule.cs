using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Customers.Administration;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Customers.Documents;
using CarRental.Application.Customers.Eligibility;
using CarRental.Application.Customers.Privacy;
using CarRental.Application.Customers.Queries;
using CarRental.Application.Customers.Registration;
using CarRental.Application.Customers.Seeding;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Customers;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Customers;

/// <summary>
/// Customers module: drivers (persons) and payers (individuals, companies), identity documents and their images
/// (private storage, logged access), consents, blacklist, eligibility, data subject rights and retention.
/// </summary>
public static class CustomersModule
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Contracts for the other modules.
        services.AddScoped<ICustomersQueries, CustomersQueries>();
        services.AddScoped<ICustomerRegistration, CustomerRegistration>();

        // Back office.
        services.AddScoped<CustomerAdministrationQueries>();
        services.AddScoped<PersonAdministrationService>();
        services.AddScoped<CompanyCustomerAdministrationService>();
        services.AddScoped<DocumentImageAccess>();
        services.AddScoped<PersonalDataService>();

        // Internals.
        services.AddScoped<DriverEligibilityChecker>();
        services.AddScoped<DocumentImageStore>();

        services.AddSettingDefinitions(CustomerSettings.All);
        services.AddOutboxHandler<DocumentImagesDiscarded, DocumentImagesDiscardedHandler>();
        services.AddRecurringJob<DocumentImageRetentionJob>();
        services.AddDataSeeder<CustomersDemoSeeder>();
        return services;
    }
}
