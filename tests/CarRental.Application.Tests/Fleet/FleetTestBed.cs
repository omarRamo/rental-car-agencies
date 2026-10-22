using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet;
using CarRental.Application.Fleet.Deadlines;
using CarRental.Application.Fleet.Photos;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Fakes;
using CarRental.Application.Tests.Fleet.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Fleet;

/// <summary>Fleet services wired on test doubles, acting as a given role; "now" is 20/10/2026 10:00 in Tunis.</summary>
internal sealed class FleetTestBed
{
    public static readonly DateTimeOffset Now = TunisTime.FromLocal(new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Unspecified));

    public FleetTestBed()
    {
        Referential.Agencies.Add(new AgencyInfo(AgencyId, "Agence Test", "Tunis", null, null, IsActive: true));
    }

    /// <summary>The agency of the fleet, which the staff actors of <see cref="As"/> work for (ADR-0067).</summary>
    public Guid AgencyId { get; } = TestUsers.AgencyId;

    public FakeAppDbContext Db { get; } = new();

    public RecordingAvailabilityLock Lock { get; } = new();

    public RecordingAvailabilityNotifier Notifier { get; } = new();

    public InMemoryFileStorage Storage { get; } = new();

    public RecordingEmailQueue Emails { get; } = new();

    public FakeIdentityQueries Identity { get; } = new();

    public FakeReferentialQueries Referential { get; } = new();

    public FakeTimeProvider Time { get; } = new(Now);

    public IUnitOfWork UnitOfWork { get; set; } = new InlineUnitOfWork();

    public static ChangeStamp Stamp(DateTimeOffset? at = null) => new(at ?? Now, "tests");

    public static IUserContext As(string role) => TestUsers.ContextFor(Guid.CreateVersion7(), $"{role.ToLowerInvariant()}@demo.local", role);

    public FleetWriter Writer(IUserContext user) => new(Db, UnitOfWork, Lock, Notifier, user, Time);

    public VehicleService Vehicles(string role = Roles.Manager)
    {
        var user = As(role);
        return new VehicleService(Db, Writer(user), user);
    }

    public VehicleMaintenanceService Maintenance(string role = Roles.Manager)
    {
        var user = As(role);
        return new VehicleMaintenanceService(Writer(user), user);
    }

    public VehicleCategoryService Categories(string role = Roles.Manager)
    {
        var user = As(role);
        return new VehicleCategoryService(Db, Writer(user), user, Referential);
    }

    public FleetPhotoService Photos(string role = Roles.Manager)
    {
        var user = As(role);
        return new FleetPhotoService(Db, Writer(user), Storage, user, NullLogger<FleetPhotoService>.Instance);
    }

    public FleetAdminQueries AdminQueries(string role = Roles.Manager) => new(Db, As(role), new DefaultSettingsReader(), Time);

    public FleetQueries Queries() => new(Db, Time);

    public DeadlineAlertJob AlertJob() =>
        new(Db, UnitOfWork, Identity, Referential, new PlainDeadlineAlertEmails(), Emails, Time, NullLogger<DeadlineAlertJob>.Instance);

    public VehicleCategory AddCategory(string code = "ECMR", FuelType fuel = FuelType.Petrol, int displayOrder = 2, bool active = true, Guid? agencyId = null)
    {
        var category = VehicleCategory.Create(
            agencyId ?? AgencyId,
            new VehicleCategoryDetails(code, $"Catégorie {code}", "Peugeot 208", 5, 5, 1, 2, fuel, Money.Tnd(600m), Money.Tnd(300m), displayOrder)).Value;
        if (!active)
        {
            category.Deactivate();
        }

        category.DequeueDomainEvents();
        Db.Fake<VehicleCategory>().Add(category);
        return category;
    }

    public Vehicle AddVehicle(VehicleCategory category, string plate = "215 TU 4512", DateOnly? documentsExpireOn = null, int mileage = 20_000)
    {
        var vehicle = Vehicle.Register(category.AgencyId, category.Id, Form(category.Id, plate).ToDetailsForTests(), mileage, Stamp(Now.AddYears(-1))).Value;
        foreach (var type in VehicleCompliance.MandatoryTypes)
        {
            vehicle.RecordDocument(new VehicleDocumentDetails(type, $"{type}-1", null, documentsExpireOn ?? new DateOnly(2027, 6, 30)), Stamp(Now.AddYears(-1))).IsSuccess.ShouldBeTrue();
        }

        vehicle.DequeueDomainEvents();
        Db.Fake<Vehicle>().Add(vehicle);
        return vehicle;
    }

    public static VehicleForm Form(Guid categoryId, string plate = "215 TU 4512") => new(
        categoryId,
        plate,
        "Peugeot",
        "208",
        2024,
        "Blanc",
        Transmission.Manual,
        FuelType.Petrol,
        5,
        1,
        2,
        new DateOnly(2024, 3, 1),
        new DateOnly(2024, 2, 20),
        null);
}

internal static class VehicleFormExtensions
{
    public static VehicleDetails ToDetailsForTests(this VehicleForm form) => new(
        form.PlateNumber,
        form.Make,
        form.Model,
        form.Year,
        form.Color,
        form.Transmission,
        form.Fuel,
        form.Seats,
        form.LargeBags,
        form.SmallBags,
        form.InServiceOn,
        form.FirstRegistrationOn);
}
