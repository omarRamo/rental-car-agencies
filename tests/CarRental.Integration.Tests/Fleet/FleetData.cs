using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Fleet;
using CarRental.Domain.Referential;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Fleet;

/// <summary>Isolated fleet data for a test: its own agency (categories and plates never collide between parallel tests).</summary>
internal static class FleetData
{
    private static int _plateSequence;

    public static UserContextSnapshot Actor(string role, string? userName = null) =>
        UserContextSnapshot.ForUser(Guid.CreateVersion7(), userName ?? $"{role.ToLowerInvariant()}@demo.local", RolePresets.PermissionsOf(role), "203.0.113.20", Guid.NewGuid().ToString("N"));

    public static UserContextSnapshot Manager => Actor(Roles.Manager, "gerant@demo.local");

    public static string NewPlate() => $"{100 + (Interlocked.Increment(ref _plateSequence) % 900)} TU {Random.Shared.Next(1000, 9999)}";

    public static async Task<Guid> NewAgencyAsync(IntegrationHost host, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var agency = Agency.Register(new AgencyDetails($"Agence flotte {Guid.NewGuid():N}", "Sousse")).Value;
        db.Set<Agency>().Add(agency);
        await db.SaveChangesAsync(cancellationToken);
        return agency.Id;
    }

    public static async Task<Guid> NewCategoryAsync(IntegrationHost host, Guid agencyId, string code, CancellationToken cancellationToken, int displayOrder = 1)
    {
        await using var scope = host.CreateScope(Manager);
        var service = scope.ServiceProvider.GetRequiredService<VehicleCategoryService>();
        var fuel = VehicleCategory.FuelOf(CarRental.Domain.Common.AcrissCode.Create(code).Value) ?? FuelType.Petrol;
        var created = await service.CreateAsync(
            new CategoryForm(code, $"Catégorie {code}", "Peugeot 208", 5, 5, 1, 2, fuel, 600m, 300m, displayOrder, agencyId),
            cancellationToken);
        created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.ToString() : null);
        return created.Value;
    }

    public static VehicleForm Form(Guid categoryId, string? plate = null, DateOnly? inServiceOn = null, decimal? guaranteedSupplement = null) => new(
        categoryId,
        plate ?? NewPlate(),
        "Renault",
        "Clio",
        2025,
        "Gris",
        Transmission.Manual,
        FuelType.Petrol,
        5,
        1,
        2,
        inServiceOn ?? new DateOnly(2025, 1, 15),
        new DateOnly(2025, 1, 10),
        guaranteedSupplement);

    /// <summary>A vehicle with its three mandatory documents valid until <paramref name="documentsExpireOn"/> (or none).</summary>
    public static async Task<Guid> NewVehicleAsync(
        IntegrationHost host,
        Guid categoryId,
        CancellationToken cancellationToken,
        DateOnly? documentsExpireOn = null,
        bool withDocuments = true,
        string? plate = null,
        decimal? guaranteedSupplement = null)
    {
        await using var scope = host.CreateScope(Manager);
        var vehicles = scope.ServiceProvider.GetRequiredService<VehicleService>();
        var registered = await vehicles.RegisterAsync(Form(categoryId, plate, guaranteedSupplement: guaranteedSupplement), 15_000, cancellationToken);
        registered.IsSuccess.ShouldBeTrue(registered.IsFailure ? registered.Error.ToString() : null);
        if (withDocuments)
        {
            foreach (var type in VehicleCompliance.MandatoryTypes)
            {
                var recorded = await vehicles.RecordDocumentAsync(
                    registered.Value,
                    new DocumentForm(type, $"{type}-{Guid.NewGuid():N}"[..20], null, documentsExpireOn ?? new DateOnly(2030, 12, 31), null, null, null),
                    cancellationToken);
                recorded.IsSuccess.ShouldBeTrue(recorded.IsFailure ? recorded.Error.ToString() : null);
            }
        }

        return registered.Value;
    }
}
