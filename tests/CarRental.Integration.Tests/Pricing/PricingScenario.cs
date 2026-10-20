using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Pricing.Administration;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using CarRental.Domain.Referential;
using CarRental.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Pricing;

/// <summary>A dedicated agency per test (tests run in parallel on one database) and the SPEC §7 reference plan.</summary>
internal sealed record PricingAgency(Guid AgencyId, Guid OfficeId, Guid AirportId);

internal static class PricingScenario
{
    public static readonly Guid ManagerId = Guid.CreateVersion7();

    public static UserContextSnapshot Manager { get; } =
        UserContextSnapshot.ForUser(ManagerId, "gerant.tarifs@demo.local", RolePresets.PermissionsOf(Roles.Manager), "203.0.113.20", "corr-pricing");

    public static UserContextSnapshot Accountant { get; } =
        UserContextSnapshot.ForUser(Guid.CreateVersion7(), "comptable.tarifs@demo.local", RolePresets.PermissionsOf(Roles.Accountant));

    public static UserContextSnapshot Agent { get; } =
        UserContextSnapshot.ForUser(Guid.CreateVersion7(), "agent.tarifs@demo.local", RolePresets.PermissionsOf(Roles.Agent));

    public static DateTimeOffset Tunis(int year, int month, int day, int hour = 10, int minute = 0) =>
        TunisTime.FromLocal(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified));

    public static async Task<PricingAgency> CreateAgencyAsync(IntegrationHost host, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var agency = Agency.Register(new AgencyDetails($"Agence tarifs {suffix}", "Tunis")).Value;
        var office = Location.Create(agency.Id, $"O{suffix}", "Agence", LocationKind.AgencyOffice).Value;
        var airport = Location.Create(agency.Id, $"A{suffix}", "Aéroport", LocationKind.Airport).Value;
        db.Set<Agency>().Add(agency);
        db.Set<Location>().AddRange(office, airport);
        await db.SaveChangesAsync(cancellationToken);
        return new PricingAgency(agency.Id, office.Id, airport.Id);
    }

    /// <summary>Runs an administration call in its own scope, like the back office does for each operation.</summary>
    public static async Task<T> AdministerAsync<T>(
        IntegrationHost host,
        Func<RatePlanAdministrationService, Task<Result<T>>> operation,
        UserContextSnapshot? actor = null)
    {
        await using var scope = host.CreateScope(actor ?? Manager);
        var result = await operation(scope.ServiceProvider.GetRequiredService<RatePlanAdministrationService>());
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    /// <summary>
    /// Creates and publishes the SPEC §7 grid for the agency (mid 110 until 30/06/2027, high 140, tiers 0/5/10/15/20 %,
    /// additional driver, child seat capped at 50.000, airport delivery), without promotion, through the service.
    /// </summary>
    public static async Task<RatePlanDetail> PublishReferencePlanAsync(IntegrationHost host, PricingAgency agency, CancellationToken cancellationToken)
    {
        var draftId = await AdministerAsync(host, s => s.CreateDraftAsync(new CreateDraftRequest(agency.AgencyId, null, "Grille de référence"), cancellationToken));
        var detail = await AdministerAsync(host, s => s.UpdateSeasonsAsync(
            draftId,
            0,
            [
                new SeasonDefinition(SeasonLevel.Mid, new DateOnly(2027, 4, 1), new DateOnly(2027, 6, 30)),
                new SeasonDefinition(SeasonLevel.High, new DateOnly(2027, 7, 1), new DateOnly(2027, 8, 31), 3),
            ],
            cancellationToken));
        detail = await AdministerAsync(host, s => s.UpdatePricesAsync(
            draftId,
            detail.Plan.Revision,
            [
                new CategoryPriceDefinition(StubFleetQueries.Ecmr, RatePeriodKey.Base, Money.Tnd(100m)),
                new CategoryPriceDefinition(StubFleetQueries.Ecmr, RatePeriodKey.ForSeason(SeasonLevel.Mid), Money.Tnd(110m)),
                new CategoryPriceDefinition(StubFleetQueries.Ecmr, RatePeriodKey.ForSeason(SeasonLevel.High), Money.Tnd(140m)),
            ],
            cancellationToken));
        detail = await AdministerAsync(host, s => s.UpdateTiersAsync(
            draftId,
            detail.Plan.Revision,
            [new(1, 0m), new(3, 5m), new(7, 10m), new(14, 15m), new(30, 20m)],
            [],
            cancellationToken));
        detail = await AdministerAsync(host, s => s.UpdateExtrasAsync(
            draftId,
            detail.Plan.Revision,
            [
                new ExtraDefinition("ADDITIONAL_DRIVER", "Conducteur additionnel", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(10m), NameEn: "Additional driver"),
                new ExtraDefinition("CHILD_SEAT", "Siège enfant", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(8m), Money.Tnd(50m), NameEn: "Child seat"),
                new ExtraDefinition("FRANCHISE_WAIVER", "Rachat de franchise", ExtraKind.FranchiseWaiver, ExtraPricingMode.PerDay, Money.Tnd(20m)),
                new ExtraDefinition("DELIVERY_AIRPORT", "Livraison aéroport", ExtraKind.LocationDelivery, ExtraPricingMode.PerRental, Money.Tnd(15m), LocationId: agency.AirportId),
            ],
            cancellationToken));
        return await AdministerAsync(host, s => s.PublishAsync(draftId, detail.Plan.Revision, cancellationToken));
    }

    public static PriceRequest ReferenceRequest(PricingAgency agency, SalesChannel channel = SalesChannel.Web) => new(
        agency.AgencyId,
        StubFleetQueries.Ecmr,
        agency.OfficeId,
        agency.OfficeId,
        Tunis(2027, 6, 28),
        Tunis(2027, 7, 3),
        channel,
        [new SelectedExtra("ADDITIONAL_DRIVER", 1), new SelectedExtra("CHILD_SEAT", 1)]);
}
