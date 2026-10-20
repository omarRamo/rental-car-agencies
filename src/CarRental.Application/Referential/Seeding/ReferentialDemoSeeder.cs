using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Seeding;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Referential.Seeding;

/// <summary>Demo agency and its three locations (agency office, Tunis-Carthage and Enfidha-Hammamet airports).</summary>
internal sealed class ReferentialDemoSeeder(IAppDbContext db) : IDataSeeder
{
    public const string DemoAgencyName = "Agence Démo Tunis";

    public SeedStage Stage => SeedStage.Demo;

    public int Order => 200;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var agency = await db.Set<Agency>().FirstOrDefaultAsync(a => a.Name == DemoAgencyName, cancellationToken);
        if (agency is null)
        {
            agency = Agency.Register(new AgencyDetails(
                Name: DemoAgencyName,
                City: "Tunis",
                LegalName: "Agence Démo Tunis SARL",
                TaxIdentifier: "0000000/A/M/000",
                Address: "1 avenue Habib Bourguiba, 1000 Tunis",
                Phone: "+216 70 000 000",
                Email: "contact@demo.local",
                Latitude: 36.799800m,
                Longitude: 10.180100m)).Value;
            db.Set<Agency>().Add(agency);
        }

        var existingCodes = await db.Set<Location>()
            .Where(l => l.AgencyId == agency.Id)
            .Select(l => l.Code)
            .ToListAsync(cancellationToken);

        (string Code, string Name, LocationKind Kind, string Address)[] locations =
        [
            ("AGC", "Agence Tunis Centre", LocationKind.AgencyOffice, "1 avenue Habib Bourguiba, 1000 Tunis"),
            ("TUN", "Aéroport Tunis-Carthage", LocationKind.Airport, "Aéroport international de Tunis-Carthage, 2035 Tunis"),
            ("NBE", "Aéroport Enfidha-Hammamet", LocationKind.Airport, "Aéroport international d'Enfidha-Hammamet, 4030 Enfidha"),
        ];

        foreach (var location in locations.Where(l => !existingCodes.Contains(l.Code)))
        {
            db.Set<Location>().Add(Location.Create(agency.Id, location.Code, location.Name, location.Kind, location.Address).Value);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
