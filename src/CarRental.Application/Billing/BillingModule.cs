using CarRental.Application.Accounting.Contracts;
using CarRental.Application.Billing.Administration;
using CarRental.Application.Billing.Claims;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Billing.Documents;
using CarRental.Application.Billing.EInvoicing;
using CarRental.Application.Billing.Fines;
using CarRental.Application.Billing.Invoicing;
using CarRental.Application.Billing.Queries;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Billing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarRental.Application.Billing;

/// <summary>
/// Billing module (package 3A, ADR-0057 to ADR-0059): invoices and credit notes (gapless numbers, frozen once issued,
/// VAT per rate like the pricing engine, stamp duty outside the VAT), the invoicing moments (closing, cancellation fee,
/// fine re-invoicing) through the outbox, the electronic invoicing port (paper transitional regime, TTN), the PDF and the
/// e-mail of the documents, traffic fines and claims, and the Reporting sources of the amounts invoiced and of the claim
/// costs. The web host provides the PDF renderers, the e-mail factory and the texts; the Infrastructure the billing lock
/// and the TTN provider.
/// </summary>
public static class BillingModule
{
    public static IServiceCollection AddBillingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Internals, one instance per operation scope.
        services.AddScoped<BillingStore>();
        services.AddScoped<InvoiceComposer>();
        services.AddScoped<BuyerResolver>();
        services.AddScoped<BillingScopes>();
        services.AddScoped<EInvoiceProviders>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IEInvoiceProvider, PaperTransitionalEInvoiceProvider>());

        // Services (their constructors are internal: built here).
        services.AddScoped(sp => new InvoicingService(
            sp.GetRequiredService<BillingStore>(),
            sp.GetRequiredService<IBookingsBillingQueries>(),
            sp.GetRequiredService<IHandoverQueries>(),
            sp.GetRequiredService<IPaymentsQueries>(),
            sp.GetRequiredService<BuyerResolver>(),
            sp.GetRequiredService<InvoiceComposer>(),
            sp.GetRequiredService<IAccountingPeriods>()));
        services.AddScoped(sp => new BillingDocumentPipeline(
            sp.GetRequiredService<IAppDbContext>(),
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<IFileStorage>(),
            sp.GetRequiredService<IBillingDocumentRenderer>(),
            sp.GetRequiredService<IBillingTexts>(),
            sp.GetRequiredService<IBillingEmailFactory>(),
            sp.GetRequiredService<IEmailQueue>(),
            sp.GetRequiredService<EInvoiceProviders>(),
            sp.GetRequiredService<IReferentialQueries>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new BillingAdministrationQueries(
            sp.GetRequiredService<BillingStore>(),
            sp.GetRequiredService<IBookingsBillingQueries>(),
            sp.GetRequiredService<IPaymentsBillingQueries>(),
            sp.GetRequiredService<IOutbox>(),
            sp.GetRequiredService<IFileStorage>()));
        services.AddScoped(sp => new FineService(
            sp.GetRequiredService<BillingStore>(),
            sp.GetRequiredService<IBookingsBillingQueries>(),
            sp.GetRequiredService<IFleetQueries>(),
            sp.GetRequiredService<ICustomersQueries>(),
            sp.GetRequiredService<IFineDesignationRenderer>(),
            sp.GetRequiredService<IDocumentAccessLog>(),
            sp.GetRequiredService<IAuditTrail>(),
            sp.GetRequiredService<InvoicingService>()));
        services.AddScoped(sp => new ClaimService(
            sp.GetRequiredService<BillingStore>(),
            sp.GetRequiredService<IFleetQueries>(),
            sp.GetRequiredService<IBookingsQueries>(),
            sp.GetRequiredService<IFileStorage>()));

        // Contracts for the other modules; the Reporting ports (registered before the Reporting module: they win).
        services.AddScoped<IBillingQueries, BillingQueries>();
        services.AddScoped<IBillingAccountingQueries, BillingAccountingQueries>();
        services.AddScoped<IInvoicedAmountsSource, InvoicedAmountsSource>();
        services.AddScoped<ICustomerDocumentProvider, BillingCustomerDocumentProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IVehicleCostsSource, ClaimsVehicleCostsSource>());

        // The invoicing moments and what follows an issue, through the outbox.
        services.AddOutboxHandler<BookingStatusChanged, BookingInvoicingHandler>();
        services.AddOutboxHandler<InvoiceIssued, BillingDocumentHandler>();
        services.AddOutboxHandler<CreditNoteIssued, BillingDocumentHandler>();
        services.AddOutboxHandler<BillingDocumentRetryRequested, BillingDocumentHandler>();
        services.AddOutboxHandler<ClaimFileDiscarded, ClaimFileDiscardedHandler>();

        services.AddSettingDefinitions(BillingSettings.All);
        return services;
    }
}
