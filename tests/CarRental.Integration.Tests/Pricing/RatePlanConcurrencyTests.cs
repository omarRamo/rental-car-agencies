using CarRental.Application.Pricing.Administration;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Pricing.PricingScenario;

namespace CarRental.Integration.Tests.Pricing;

/// <summary>Concurrent edits of rate plans on SQL Server end in business errors, never in a server error.</summary>
public sealed class RatePlanConcurrencyTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_managers_creating_the_draft_at_once_get_one_draft_and_one_conflict()
    {
        var agency = await CreateAgencyAsync(host, Ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => CreateDraftAsync(agency.AgencyId)));

        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Single(r => r.IsFailure).Error.ShouldBe(PricingErrors.DraftAlreadyExists);
    }

    [Fact]
    public async Task A_delivery_extra_cannot_target_a_location_of_another_agency()
    {
        var agency = await CreateAgencyAsync(host, Ct);
        var other = await CreateAgencyAsync(host, Ct);
        var draftId = await AdministerAsync(host, s => s.CreateDraftAsync(new CreateDraftRequest(agency.AgencyId, null, "Grille livraison"), Ct));

        await using var scope = host.CreateScope(Manager);
        var service = scope.ServiceProvider.GetRequiredService<RatePlanAdministrationService>();
        var foreign = await service.UpdateExtrasAsync(
            draftId,
            0,
            [new ExtraDefinition("DELIVERY_OTHER", "Livraison", ExtraKind.LocationDelivery, ExtraPricingMode.PerRental, Money.Tnd(15m), LocationId: other.AirportId)],
            Ct);
        var own = await service.UpdateExtrasAsync(
            draftId,
            0,
            [new ExtraDefinition("DELIVERY_OWN", "Livraison", ExtraKind.LocationDelivery, ExtraPricingMode.PerRental, Money.Tnd(15m), LocationId: agency.AirportId)],
            Ct);

        foreign.Error.ShouldBe(PricingErrors.ExtraInvalid);
        own.IsSuccess.ShouldBeTrue(own.IsFailure ? own.Error.ToString() : null);
    }

    private async Task<Result<Guid>> CreateDraftAsync(Guid agencyId)
    {
        await using var scope = host.CreateScope(Manager);
        return await scope.ServiceProvider.GetRequiredService<RatePlanAdministrationService>()
            .CreateDraftAsync(new CreateDraftRequest(agencyId, null, "Grille concurrente"), Ct);
    }
}
