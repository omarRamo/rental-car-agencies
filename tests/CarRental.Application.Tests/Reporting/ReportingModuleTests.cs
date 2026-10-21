using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting;
using CarRental.Application.Reporting.Contracts;
using CarRental.Application.Reporting.Indicators;
using CarRental.Application.Reporting.Snapshots;
using CarRental.Application.Reporting.Sources;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Tests.Reporting;

/// <summary>The registrations of the Reporting module: services, nightly job, demo seed, settings and ports (ADR-0046).</summary>
public sealed class ReportingModuleTests
{
    [Fact]
    public void The_module_registers_its_services_its_job_its_seed_and_its_settings()
    {
        var services = new ServiceCollection().AddApplication();

        services.ShouldContain(d => d.ServiceType == typeof(SnapshotService));
        services.ShouldContain(d => d.ServiceType == typeof(ReportQueries));
        services.ShouldContain(d => d.ServiceType == typeof(RecurringJobDescriptor)
                                    && ((RecurringJobDescriptor)d.ImplementationInstance!).JobId == "reporting.daily_snapshot"
                                    && ((RecurringJobDescriptor)d.ImplementationInstance!).CronExpression == "0 3 * * *");
        services.ShouldContain(d => d.ServiceType == typeof(IDataSeeder) && d.ImplementationType == typeof(ReportingDemoSeeder));

        using var provider = services.BuildServiceProvider();
        var keys = provider.GetRequiredService<ISettingDefinitionCatalog>().All.Select(d => d.Key).ToList();
        keys.ShouldContain("reporting.snapshot_refresh_days");
        keys.ShouldContain("reporting.sell_hint_months");
    }

    [Fact]
    public void The_fleet_and_claim_costs_the_channel_commissions_the_return_charges_and_the_invoicing_have_their_source()
    {
        var services = new ServiceCollection().AddApplication();

        services.Where(d => d.ServiceType == typeof(IVehicleCostsSource)).Select(d => d.ImplementationType)
            .ShouldBe([typeof(CarRental.Application.Billing.Queries.ClaimsVehicleCostsSource), typeof(FleetVehicleCostsSource)], ignoreOrder: true);
        services.Single(d => d.ServiceType == typeof(IChannelCommissionSource)).ImplementationType.ShouldBe(typeof(CarRental.Application.Channels.Queries.ChannelCommissionReportingSource));
        services.Single(d => d.ServiceType == typeof(IInvoicedAmountsSource)).ImplementationType.ShouldBe(typeof(CarRental.Application.Billing.Queries.InvoicedAmountsSource));
        services.Single(d => d.ServiceType == typeof(IBookingChargesSource)).ImplementationType.ShouldBe(typeof(CarRental.Application.Bookings.Handover.ReturnChargesReportingSource));
    }

    [Fact]
    public void A_provider_registered_by_a_module_before_reporting_wins_and_a_second_cost_source_adds_up()
    {
        var services = new ServiceCollection();
        services.AddScoped<IInvoicedAmountsSource, ProvidedInvoices>();
        services.AddScoped<IVehicleCostsSource, ProvidedCosts>();

        services.AddReportingModule();

        services.Single(d => d.ServiceType == typeof(IInvoicedAmountsSource)).ImplementationType.ShouldBe(typeof(ProvidedInvoices));
        services.Where(d => d.ServiceType == typeof(IVehicleCostsSource)).Select(d => d.ImplementationType)
            .ShouldBe([typeof(ProvidedCosts), typeof(FleetVehicleCostsSource)], ignoreOrder: true);
    }

    [Fact]
    public async Task The_empty_ports_return_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var agency = Guid.CreateVersion7();

        (await new UnconnectedChannelCommissions().GetCommissionsAsync(agency, [Guid.CreateVersion7()], ct)).ShouldBeEmpty();
        (await new UnconnectedBookingCharges().GetChargesDueAsync(agency, [Guid.CreateVersion7()], ct)).ShouldBeEmpty();
        (await new UnconnectedInvoicedAmounts().GetInvoicedAsync(agency, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), ct)).ShouldBe(InvoicedAmountsInfo.None);
    }

    private sealed class ProvidedInvoices : IInvoicedAmountsSource
    {
        public Task<InvoicedAmountsInfo> GetInvoicedAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult(new InvoicedAmountsInfo(Money.Tnd(1m), Money.Zero, Money.Tnd(1m), 1, 0));
    }

    private sealed class ProvidedCosts : IVehicleCostsSource
    {
        public string Name => "expenses";

        public Task<IReadOnlyList<VehicleCostInfo>> GetCostsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VehicleCostInfo>>([]);
    }
}
