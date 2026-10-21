using System.Globalization;
using CarRental.Application.Billing.Administration;
using CarRental.Application.Billing.Claims;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Billing.Documents;
using CarRental.Application.Billing.Fines;
using CarRental.Application.Billing.Invoicing;
using CarRental.Application.Billing.Queries;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Billing;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Tests.Billing;

/// <summary>The registrations of the Billing module, its settings and the mirror of its contract enums.</summary>
public sealed class BillingModuleTests
{
    [Fact]
    public void The_module_registers_its_services_its_handlers_its_sources_and_its_settings()
    {
        var services = new ServiceCollection().AddApplication();

        foreach (var type in new[] { typeof(InvoicingService), typeof(BillingDocumentPipeline), typeof(BillingAdministrationQueries), typeof(FineService), typeof(ClaimService) })
        {
            services.ShouldContain(d => d.ServiceType == type && d.Lifetime == ServiceLifetime.Scoped, type.Name);
        }

        services.Single(d => d.ServiceType == typeof(IBillingQueries)).ImplementationType.ShouldBe(typeof(BillingQueries));
        services.Single(d => d.ServiceType == typeof(IInvoicedAmountsSource)).ImplementationType.ShouldBe(typeof(InvoicedAmountsSource), "billing wins over the empty default of the reports");
        services.Where(d => d.ServiceType == typeof(IVehicleCostsSource)).Select(d => d.ImplementationType).ShouldContain(typeof(ClaimsVehicleCostsSource));
        services.ShouldContain(d => d.ServiceType == typeof(IOutboxHandler<BookingStatusChanged>) && d.ImplementationType == typeof(BookingInvoicingHandler));
        services.ShouldContain(d => d.ServiceType == typeof(IOutboxHandler<InvoiceIssued>) && d.ImplementationType == typeof(BillingDocumentHandler));
        services.ShouldContain(d => d.ServiceType == typeof(IOutboxHandler<CreditNoteIssued>) && d.ImplementationType == typeof(BillingDocumentHandler));
        services.ShouldContain(d => d.ServiceType == typeof(IOutboxHandler<BillingDocumentRetryRequested>) && d.ImplementationType == typeof(BillingDocumentHandler));
        services.ShouldContain(d => d.ServiceType == typeof(IOutboxHandler<ClaimFileDiscarded>));

        using var provider = services.BuildServiceProvider();
        var keys = provider.GetRequiredService<ISettingDefinitionCatalog>().All.Select(d => d.Key).ToList();
        keys.ShouldContain("billing.issue_automatically");
        keys.ShouldContain("billing.einvoice_ttn");
        keys.ShouldContain("billing.einvoice_transitional_mention");
        keys.ShouldContain("billing.invoice_mentions");
        keys.ShouldContain("billing.fine_management_fee");
        keys.ShouldContain("billing.stamp_duty");
    }

    [Theory]
    [InlineData(typeof(BillingDocumentType), typeof(SalesDocumentTypeInfo))]
    [InlineData(typeof(InvoiceKind), typeof(InvoiceKindInfo))]
    [InlineData(typeof(InvoiceLineNature), typeof(InvoiceLineNatureInfo))]
    [InlineData(typeof(BuyerKind), typeof(BuyerKindInfo))]
    [InlineData(typeof(EInvoiceMode), typeof(EInvoiceModeInfo))]
    [InlineData(typeof(EInvoiceStatus), typeof(EInvoiceStatusInfo))]
    public void Contract_enums_mirror_the_domain_enums(Type domain, Type contract)
    {
        var domainValues = Enum.GetValues(domain).Cast<object>().Select(v => (Enum.GetName(domain, v), Convert.ToInt32(v, CultureInfo.InvariantCulture)));
        var contractValues = Enum.GetValues(contract).Cast<object>().Select(v => (Enum.GetName(contract, v), Convert.ToInt32(v, CultureInfo.InvariantCulture)));

        contractValues.ShouldBe(domainValues);
    }

    [Fact]
    public void The_quote_line_kinds_of_the_bookings_contract_mirror_the_domain()
    {
        var domain = Enum.GetValues<Domain.Bookings.Quotes.QuoteLineKind>().Select(v => (v.ToString(), (int)v));
        var contract = Enum.GetValues<BookingQuoteLineKindInfo>().Select(v => (v.ToString(), (int)v));

        contract.ShouldBe(domain);
    }
}
