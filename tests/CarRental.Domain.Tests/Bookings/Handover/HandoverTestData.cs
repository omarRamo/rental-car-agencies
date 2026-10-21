using System.Security.Cryptography;
using System.Text;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Bookings.Handover;

/// <summary>Inspections of a rental file built only through the aggregate's own methods, at a fixed instant.</summary>
internal static class HandoverTestData
{
    public static readonly DateTimeOffset Now = TunisTime.FromLocal(new DateTime(2027, 6, 11, 10, 0, 0, DateTimeKind.Unspecified));

    public static readonly Guid AgencyId = Guid.CreateVersion7();

    public static readonly Guid BookingId = Guid.CreateVersion7();

    public static readonly Guid VehicleId = Guid.CreateVersion7();

    public static readonly Guid OtherVehicleId = Guid.CreateVersion7();

    public static readonly Guid MainDriverId = Guid.CreateVersion7();

    public static readonly Guid SecondDriverId = Guid.CreateVersion7();

    public static BookingActor Agent { get; } = new(Guid.CreateVersion7(), "agent@demo.local");

    public static BookingActor Manager { get; } = new(Guid.CreateVersion7(), "gerant@demo.local");

    public static IReadOnlyCollection<Guid> Drivers { get; } = [MainDriverId];

    public static AccessoryChecklist FullKit { get; } = AccessoryChecklist.Create(true, true, true, true, 2, 0, false, true).Value;

    private static int _files;

    /// <summary>A stored image reference with a unique path and its SHA-256.</summary>
    public static HandoverFile Image(string contentType = HandoverFile.Jpeg)
    {
        var path = $"bookings/{BookingId:D}/file-{Interlocked.Increment(ref _files)}.bin";
        return HandoverFile.Create(path, contentType, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path))), 2048);
    }

    public static HandoverFile Png() => Image(HandoverFile.Png);

    public static HandoverFile Pdf() => Image(HandoverFile.Pdf);

    public static DamageInput Scratch(DamageView view = DamageView.Left, decimal x = 40m, decimal y = 55m, bool preExisting = false) =>
        new(view, x, y, DamageType.Scratch, DamageSeverity.Minor, "Rayure portière", preExisting);

    /// <summary>A departure draft with its vehicle confirmed (last known mileage 12 000 km) and, optionally, known damages.</summary>
    public static Inspection Departure(IEnumerable<DamageMark>? knownDamages = null, int lastKnownMileage = 12_000)
    {
        var inspection = Inspection.StartDeparture(AgencyId, BookingId, Agent, Now);
        inspection.ConfirmVehicle(VehicleId, lastKnownMileage, knownDamages ?? [], Agent, Now).IsSuccess.ShouldBeTrue();
        return inspection;
    }

    /// <summary>A departure draft ready to be completed: originals, readings, the eight guided photos and both signatures.</summary>
    public static Inspection ReadyDeparture(IEnumerable<DamageMark>? knownDamages = null)
    {
        var inspection = Departure(knownDamages);
        inspection.RecordOriginals([new DriverOriginals(MainDriverId, true, true)], Drivers).IsSuccess.ShouldBeTrue();
        inspection.RecordReadings(12_340, 8, FullKit).IsSuccess.ShouldBeTrue();
        foreach (var slot in PhotoSlots.Guided)
        {
            inspection.AddPhoto(Guid.CreateVersion7(), slot, Image(), Image(), null, Agent, Now).IsSuccess.ShouldBeTrue();
        }

        SignBoth(inspection);
        return inspection;
    }

    public static void SignBoth(Inspection inspection)
    {
        inspection.Sign(Guid.CreateVersion7(), SignatureRole.Customer, "Sami Trabelsi", Png(), Agent, Now).IsSuccess.ShouldBeTrue();
        inspection.Sign(Guid.CreateVersion7(), SignatureRole.Agent, "Agent Démo", Png(), Agent, Now).IsSuccess.ShouldBeTrue();
    }

    /// <summary>A completed departure (12 340 km, full tank) with one pre-existing scratch.</summary>
    public static Inspection CompletedDeparture()
    {
        var inspection = ReadyDeparture();
        inspection.AddDamage(Scratch(DamageView.Front, 20m, 30m), Agent, Now).IsSuccess.ShouldBeTrue();
        SignBoth(inspection);
        inspection.CompleteDeparture(VehicleId, Drivers, Now, Agent, Now).IsSuccess.ShouldBeTrue();
        return inspection;
    }

    /// <summary>A return draft after <paramref name="departure"/>.</summary>
    public static Inspection Return(Inspection? departure = null)
    {
        var from = departure ?? CompletedDeparture();
        return Inspection.StartReturn(AgencyId, BookingId, VehicleId, from.Mileage!.Value, from.Damages, Agent, Now.AddDays(3)).Value;
    }

    public static ReturnChargePolicy Policy(int includedKmPerDay = 200, decimal extraKm = 0.300m, decimal refuel = 15m, decimal cleaning = 30m) =>
        new(includedKmPerDay, Money.Tnd(extraKm), Money.Tnd(refuel), Money.Tnd(cleaning), 0.19m);
}
