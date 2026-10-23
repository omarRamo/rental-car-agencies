using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Seeding;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Referential.Seeding;

/// <summary>
/// The two demo agencies and their locations: Tunis (agency office, Tunis-Carthage and Enfidha-Hammamet airports), the
/// main one, and Sousse (agency office and Monastir airport), which shows that the agencies never see each other
/// (ADR-0067). Each agency is then provisioned like an agency registered by the support.
/// </summary>
internal sealed class ReferentialDemoSeeder(IAppDbContext db, IServiceScopeFactory scopeFactory) : IDataSeeder
{
    public const string DemoAgencyName = "Agence Démo Tunis";

    public const string SecondDemoAgencyName = "Agence Démo Sousse";

    public SeedStage Stage => SeedStage.Demo;

    public int Order => 200;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var tunis = await EnsureAgencyAsync(
            new AgencyDetails(
                Name: DemoAgencyName,
                City: "Tunis",
                LegalName: "Agence Démo Tunis SARL",
                TaxIdentifier: "0000000/A/M/000",
                Address: "1 avenue Habib Bourguiba, 1000 Tunis",
                Phone: "+216 70 000 000",
                Email: "contact@demo.local",
                Latitude: 36.799800m,
                Longitude: 10.180100m),
            [
                ("AGC", "Agence Tunis Centre", LocationKind.AgencyOffice, "1 avenue Habib Bourguiba, 1000 Tunis"),
                ("TUN", "Aéroport Tunis-Carthage", LocationKind.Airport, "Aéroport international de Tunis-Carthage, 2035 Tunis"),
                ("NBE", "Aéroport Enfidha-Hammamet", LocationKind.Airport, "Aéroport international d'Enfidha-Hammamet, 4030 Enfidha"),
            ],
            cancellationToken);
        var sousse = await EnsureAgencyAsync(
            new AgencyDetails(
                Name: SecondDemoAgencyName,
                City: "Sousse",
                LegalName: "Agence Démo Sousse SARL",
                TaxIdentifier: "1111111/B/M/000",
                Address: "12 boulevard du 14 Janvier, 4000 Sousse",
                Phone: "+216 73 000 000",
                Email: "sousse@demo.local",
                Latitude: 35.828800m,
                Longitude: 10.640500m),
            [
                ("SOU", "Agence Sousse Centre", LocationKind.AgencyOffice, "12 boulevard du 14 Janvier, 4000 Sousse"),
                ("MIR", "Aéroport Monastir Habib-Bourguiba", LocationKind.Airport, "Aéroport international Monastir Habib-Bourguiba, 5065 Monastir"),
            ],
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await AgencyProvisioningSeeder.ProvisionAsync(scopeFactory, tunis.Id, cancellationToken);
        await AgencyProvisioningSeeder.ProvisionAsync(scopeFactory, sousse.Id, cancellationToken);
    }

    private async Task<Agency> EnsureAgencyAsync(
        AgencyDetails details,
        (string Code, string Name, LocationKind Kind, string Address)[] locations,
        CancellationToken cancellationToken)
    {
        var agency = await db.Set<Agency>().FirstOrDefaultAsync(a => a.Name == details.Name, cancellationToken);
        if (agency is null)
        {
            agency = Agency.Register(details).Value;
            db.Set<Agency>().Add(agency);
        }

        var existingCodes = await db.Set<Location>()
            .Where(l => l.AgencyId == agency.Id)
            .Select(l => l.Code)
            .ToListAsync(cancellationToken);

        foreach (var location in locations.Where(l => !existingCodes.Contains(l.Code)))
        {
            db.Set<Location>().Add(Location.Create(agency.Id, location.Code, location.Name, location.Kind, location.Address).Value);
        }

        return agency;
    }
}
