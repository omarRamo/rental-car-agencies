using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Parties;
using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Common.Email;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Bookings.Handover;

/// <summary>
/// Registration of the departure and return (ADR-0041, ADR-0042), part of the Bookings module. The web host provides
/// <see cref="IRentalContractRenderer"/> (PDF) and <see cref="IRentalContractEmailFactory"/> (localized e-mail).
/// </summary>
internal static class HandoverServiceCollectionExtensions
{
    public static IServiceCollection AddBookingHandovers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<HandoverStore>();
        services.AddScoped<HandoverBalances>();
        services.AddScoped<ReturnChargesCalculator>();
        services.AddScoped<ContractDocumentBuilder>();
        services.AddScoped<IHandoverQueries, HandoverContractQueries>();
        services.AddScoped<IBookingChargesSource, ReturnChargesReportingSource>();

        // Back office services (their constructors are internal: built here).
        services.AddScoped(sp => new HandoverInspectionService(sp.GetRequiredService<HandoverStore>()));
        services.AddScoped(sp => new DepartureService(
            sp.GetRequiredService<HandoverStore>(),
            sp.GetRequiredService<RentalLifecycleService>(),
            sp.GetRequiredService<HandoverBalances>(),
            sp.GetRequiredService<ContractDocumentBuilder>(),
            sp.GetRequiredService<IRentalContractRenderer>(),
            sp.GetRequiredService<IRentalContractEmailFactory>(),
            sp.GetRequiredService<IEmailQueue>(),
            sp.GetRequiredService<IFleetQueries>(),
            sp.GetRequiredService<IFleetMileage>(),
            sp.GetRequiredService<PartyDirectory>(),
            sp.GetRequiredService<ICustomersQueries>(),
            sp.GetRequiredService<IReferentialQueries>(),
            sp.GetRequiredService<QuoteStore>()));
        services.AddScoped(sp => new ReturnService(
            sp.GetRequiredService<HandoverStore>(),
            sp.GetRequiredService<RentalLifecycleService>(),
            sp.GetRequiredService<HandoverBalances>(),
            sp.GetRequiredService<ReturnChargesCalculator>(),
            sp.GetRequiredService<IFleetQueries>(),
            sp.GetRequiredService<IFleetMileage>(),
            sp.GetRequiredService<IDeposits>(),
            sp.GetRequiredService<IReferentialQueries>(),
            sp.GetRequiredService<QuoteStore>()));
        services.AddScoped(sp => new HandoverQueries(
            sp.GetRequiredService<HandoverStore>(),
            sp.GetRequiredService<IBookingsQueries>(),
            sp.GetRequiredService<IFleetQueries>(),
            sp.GetRequiredService<IReferentialQueries>(),
            sp.GetRequiredService<IHandoverQueries>(),
            sp.GetRequiredService<HandoverBalances>()));

        services.AddSettingDefinitions(HandoverSettings.All);
        return services;
    }
}
