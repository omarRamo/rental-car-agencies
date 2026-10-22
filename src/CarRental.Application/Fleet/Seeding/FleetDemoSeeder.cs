using System.Globalization;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Fleet.Seeding;

/// <summary>
/// Demo fleets (development and recette only). The main demo agency gets 5 ACRISS categories and 30 fictitious vehicles
/// with documents, an oil change plan and economics; dates are relative to the first seeding day: one insurance expires
/// within 7 days, one road tax within 30 days, one technical inspection has expired (vehicle not compliant), one
/// maintenance is planned next week, one oil change is due soon and one vehicle is out of service. The second demo
/// agency gets a small fleet of its own (2 categories, 5 vehicles with valid documents), so that its public site and back
/// office are usable (ADR-0067). Idempotent: missing categories and plates only.
/// </summary>
internal sealed partial class FleetDemoSeeder(
    IAppDbContext db,
    IReferentialQueries referential,
    TimeProvider timeProvider,
    ILogger<FleetDemoSeeder> logger) : IDataSeeder
{
    /// <summary>Name of the demo agency created by the Referential demo seeder (order 200).</summary>
    public const string DemoAgencyName = "Agence Démo Tunis";

    /// <summary>Name of the second demo agency created by the Referential demo seeder.</summary>
    public const string SecondDemoAgencyName = "Agence Démo Sousse";

    private const string Actor = "system:seed";

    private static readonly CategorySeed[] Categories =
    [
        new("MCMR", "Mini", "Kia Picanto", 4, 5, 1, 1, FuelType.Petrol, 500m, 250m, 1, 42_000m, ["Kia Picanto", "Kia Picanto", "Kia Picanto", "Hyundai i10", "Hyundai i10", "Hyundai i10"]),
        new("ECMR", "Économique", "Peugeot 208", 5, 5, 1, 2, FuelType.Petrol, 600m, 300m, 2, 52_000m, ["Peugeot 208", "Peugeot 208", "Peugeot 208", "Peugeot 208", "Renault Clio", "Renault Clio", "Renault Clio", "Renault Clio"]),
        new("EDAR", "Économique automatique", "Hyundai Accent", 5, 4, 2, 1, FuelType.Petrol, 700m, 350m, 3, 63_000m, ["Hyundai Accent", "Hyundai Accent", "Hyundai Accent", "Hyundai Accent", "Hyundai Accent", "Hyundai Accent"]),
        new("CDMR", "Compacte", "Volkswagen Golf", 5, 5, 2, 2, FuelType.Diesel, 800m, 400m, 4, 82_000m, ["Volkswagen Golf", "Volkswagen Golf", "Volkswagen Golf", "Peugeot 308", "Peugeot 308"]),
        new("IFMR", "SUV intermédiaire", "Dacia Duster", 5, 5, 2, 2, FuelType.Diesel, 1000m, 500m, 5, 98_000m, ["Dacia Duster", "Dacia Duster", "Dacia Duster", "Hyundai Tucson", "Hyundai Tucson"]),
    ];

    /// <summary>The fleet of the second demo agency: the same codes as the main one, its own vehicles and deposits.</summary>
    private static readonly CategorySeed[] SecondAgencyCategories =
    [
        new("ECMR", "Économique", "Peugeot 208", 5, 5, 1, 2, FuelType.Petrol, 550m, 275m, 1, 50_000m, ["Peugeot 208", "Peugeot 208", "Renault Clio"]),
        new("CDMR", "Compacte", "Volkswagen Golf", 5, 5, 2, 2, FuelType.Diesel, 750m, 375m, 2, 80_000m, ["Volkswagen Golf", "Peugeot 308"]),
    ];

    private static readonly string[] Colors = ["Blanc", "Gris", "Noir", "Rouge", "Bleu", "Argent"];

    public SeedStage Stage => SeedStage.Demo;

    public int Order => 400;

    /// <summary>Index (0-29) of the vehicles of the main demo agency with a particular situation.</summary>
    internal static class Special
    {
        public const int OilChangeDueSoon = 5;
        public const int InsuranceWithin7Days = 3;
        public const int RoadTaxWithin30Days = 9;
        public const int ExpiredTechnicalInspection = 16;
        public const int MaintenanceNextWeek = 22;
        public const int OutOfService = 27;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var agencies = await referential.GetAgenciesAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var stamp = new ChangeStamp(now, Actor);
        var today = TunisTime.DateOf(now);

        var created = 0;
        foreach (var (name, seeds, plateOf, situations) in new (string, CategorySeed[], Func<int, string>, bool)[]
        {
            (DemoAgencyName, Categories, PlateOf, true),
            (SecondDemoAgencyName, SecondAgencyCategories, SecondAgencyPlateOf, false),
        })
        {
            if (agencies.FirstOrDefault(a => a.Name == name) is not { } agency)
            {
                LogNoAgency(name);
                continue;
            }

            created += await AddFleetAsync(agency.Id, seeds, plateOf, situations, today, stamp, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (created > 0)
        {
            LogSeeded(created);
        }
    }

    /// <summary>Fictitious Tunisian-style plate "2xx TU nnnn" of the main demo agency, unique per index.</summary>
    internal static string PlateOf(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{201 + (index * 3)} TU {1000 + (index * 2731 % 9000):D4}");

    /// <summary>Fictitious plate "1xx TU nnnn" of the second demo agency, unique per index.</summary>
    internal static string SecondAgencyPlateOf(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{150 + (index * 7)} TU {5000 + (index * 1373 % 4000):D4}");

    /// <summary>
    /// The missing categories and vehicles of an agency; <paramref name="situations"/> gives the vehicles their particular
    /// cases (deadlines, maintenance, out of service), otherwise every document is valid for months.
    /// </summary>
    private async Task<int> AddFleetAsync(
        Guid agencyId,
        CategorySeed[] seeds,
        Func<int, string> plateOf,
        bool situations,
        DateOnly today,
        ChangeStamp stamp,
        CancellationToken cancellationToken)
    {
        var categories = await db.Set<VehicleCategory>().Where(c => c.AgencyId == agencyId).ToListAsync(cancellationToken);
        var plates = (await db.Set<Vehicle>().Where(v => v.AgencyId == agencyId).Select(v => v.PlateNumber).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var index = 0;
        var created = 0;
        foreach (var seed in seeds)
        {
            var category = categories.FirstOrDefault(c => c.Code.Value == seed.Code);
            if (category is null)
            {
                category = VehicleCategory.Create(agencyId, seed.ToDetails()).Value;
                db.Set<VehicleCategory>().Add(category);
            }

            foreach (var model in seed.Models)
            {
                var plate = plateOf(index);
                if (!plates.Contains(plate))
                {
                    db.Set<Vehicle>().Add(CreateVehicle(index, plate, model, seed, category, situations, today, stamp));
                    created++;
                }

                index++;
            }
        }

        return created;
    }

    private static Vehicle CreateVehicle(
        int index,
        string plate,
        string model,
        CategorySeed seed,
        VehicleCategory category,
        bool situations,
        DateOnly today,
        ChangeStamp stamp)
    {
        var inServiceOn = today.AddDays(-(150 + (index * 37)));
        var parts = model.Split(' ', 2);
        var details = new VehicleDetails(
            plate,
            parts[0],
            parts[1],
            inServiceOn.Year,
            Colors[index % Colors.Length],
            category.Transmission,
            seed.Fuel,
            seed.Seats,
            seed.LargeBags,
            seed.SmallBags,
            inServiceOn,
            inServiceOn.AddDays(-(index % 3 * 10)));
        var mileage = 8_000 + (index * 9_137 % 87_000);
        var vehicle = Vehicle.Register(category.AgencyId, category.Id, details, mileage, stamp).Value;

        // The particular case of the vehicle, when the fleet shows them (see Special).
        int? situation = situations ? index : null;
        var insuranceExpiry = situation == Special.InsuranceWithin7Days ? today.AddDays(5) : today.AddDays(60 + (index * 11 % 250));
        var roadTaxExpiry = situation == Special.RoadTaxWithin30Days ? today.AddDays(20) : today.AddDays(90 + (index * 13 % 200));
        var inspectionExpiry = situation == Special.ExpiredTechnicalInspection ? today.AddDays(-3) : today.AddDays(100 + (index * 17 % 300));
        Record(vehicle, VehicleDocumentType.Insurance, $"POL-DEMO-{index + 1:D4}", insuranceExpiry, "Assureur démo", 1_100m + (index * 25), stamp);
        Record(vehicle, VehicleDocumentType.RoadTax, $"VIG-DEMO-{index + 1:D5}", roadTaxExpiry, null, 180m, stamp);
        Record(vehicle, VehicleDocumentType.TechnicalInspection, $"VT-DEMO-{index + 1:D6}", inspectionExpiry, "Centre de visite démo", 45m, stamp);
        vehicle.RecordDocument(
            new VehicleDocumentDetails(VehicleDocumentType.RegistrationCard, $"CG-DEMO-{index + 1:D6}", details.FirstRegistrationOn, null),
            stamp);

        vehicle.SetMaintenancePlan(
            new MaintenancePlanDetails(
                MaintenanceType.OilChange,
                10_000,
                12,
                LastDoneOn: Max(today.AddDays(-(index * 11 % 300)), inServiceOn),
                LastDoneMileage: situation == Special.OilChangeDueSoon ? mileage - 9_600 : Math.Max(0, mileage - (index * 733 % 9_000)),
                Label: "Vidange"),
            stamp);

        var price = seed.BasePrice + (index % 4 * 1_500m);
        var financing = (index % 3) switch
        {
            0 => new VehicleEconomicsDetails(Money.Tnd(price), inServiceOn, FinancingType.Cash, null, null, 5, Money.Tnd(price * 0.3m)),
            1 => new VehicleEconomicsDetails(Money.Tnd(price), inServiceOn, FinancingType.Credit, Money.Tnd(price / 48m * 1.12m), 48, 5, Money.Tnd(price * 0.3m)),
            _ => new VehicleEconomicsDetails(Money.Tnd(price), inServiceOn, FinancingType.Leasing, Money.Tnd(price / 36m * 0.85m), 36, 5, Money.Tnd(price * 0.3m)),
        };
        vehicle.SetEconomics(financing, stamp);

        if (model is "Hyundai Tucson" or "Volkswagen Golf")
        {
            vehicle.SetGuaranteedModelSupplement(Money.Tnd(model == "Hyundai Tucson" ? 25m : 15m), stamp);
        }

        if (situation == Special.MaintenanceNextWeek)
        {
            var start = TunisTime.FromLocal(today.AddDays(7), new TimeOnly(8, 0));
            vehicle.ScheduleMaintenance(
                new MaintenanceDetails(MaintenanceType.Brakes, start, TunisTime.FromLocal(today.AddDays(8), new TimeOnly(18, 0)), "Plaquettes et disques avant", "Garage démo", Money.Tnd(420m)),
                stamp);
        }

        if (situation == Special.OutOfService)
        {
            vehicle.ChangeStatus(VehicleStatus.OutOfService, "Accident : en attente de l'expertise de l'assureur", stamp);
        }

        return vehicle;
    }

    private static void Record(Vehicle vehicle, VehicleDocumentType type, string number, DateOnly expiresOn, string? provider, decimal cost, ChangeStamp stamp) =>
        vehicle.RecordDocument(
            new VehicleDocumentDetails(type, number, expiresOn.AddYears(-1).AddDays(1), expiresOn, provider, Money.Tnd(cost)),
            stamp);

    private static DateOnly Max(DateOnly left, DateOnly right) => left > right ? left : right;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo agency {Agency} not found: no demo fleet.")]
    private partial void LogNoAgency(string agency);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} demo vehicles created.")]
    private partial void LogSeeded(int count);

    private sealed record CategorySeed(
        string Code,
        string Name,
        string ExampleModel,
        int Seats,
        int Doors,
        int LargeBags,
        int SmallBags,
        FuelType Fuel,
        decimal Deposit,
        decimal ReducedDeposit,
        int DisplayOrder,
        decimal BasePrice,
        string[] Models)
    {
        public VehicleCategoryDetails ToDetails() =>
            new(Code, Name, ExampleModel, Seats, Doors, LargeBags, SmallBags, Fuel, Money.Tnd(Deposit), Money.Tnd(ReducedDeposit), DisplayOrder);
    }
}
