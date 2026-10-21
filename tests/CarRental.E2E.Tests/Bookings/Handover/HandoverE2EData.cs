using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Handover;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Pricing.Administration;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using CarRental.Domain.Pricing;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.E2E.Tests.Bookings.Handover;

/// <summary>A confirmed file of a test agency of its own, its vehicle assigned, picked up now.</summary>
internal sealed record HandoverE2EFile(Guid AgencyId, Guid BookingId, string Number, Guid DriverId, string DriverName, Guid VehicleId, Money Total, Money Deposit);

/// <summary>
/// Arrangements of the hand-over browser tests through the services (tests run in parallel: each one has its agency,
/// category, vehicle, plan and driver), and the test photos (real PNG images the browser decodes and reduces).
/// </summary>
internal static class HandoverE2EData
{
    private static int _plateSequence = Random.Shared.Next(1_000);

    public static UserContextSnapshot Actor(string role) =>
        UserContextSnapshot.ForUser(Guid.CreateVersion7(), $"{role.ToLowerInvariant()}-arrange-{Guid.NewGuid():N}@tests.local", RolePresets.PermissionsOf(role));

    /// <summary>
    /// A confirmed file picked up now for one day: agency, ECMR category, a vehicle at 15 000 km, a plan at 100 a day. The
    /// category asks for no security deposit: the browser tests share one database, and the deposits list of the payments
    /// screen is checked elsewhere as the only deposits of the day.
    /// </summary>
    public static async Task<HandoverE2EFile> ConfirmedAsync(E2EHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var manager = Actor(Roles.Manager);
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        Guid agencyId;
        Guid officeId;
        await using (var scope = Scope(host, null))
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var agency = Agency.Register(new AgencyDetails($"Agence remise {suffix}", "Tunis")).Value;
            var office = Location.Create(agency.Id, $"O{suffix}", "Agence", LocationKind.AgencyOffice).Value;
            db.Set<Agency>().Add(agency);
            db.Set<Location>().Add(office);
            await db.SaveChangesAsync();
            (agencyId, officeId) = (agency.Id, office.Id);
        }

        Guid categoryId;
        Guid vehicleId;
        await using (var scope = Scope(host, manager))
        {
            categoryId = Ok(await scope.ServiceProvider.GetRequiredService<VehicleCategoryService>().CreateAsync(
                new CategoryForm("ECMR", "Catégorie ECMR", "Peugeot 208", 5, 5, 1, 2, FuelType.Petrol, 0m, 0m, 1, agencyId)));
            var vehicles = scope.ServiceProvider.GetRequiredService<VehicleService>();
            vehicleId = Ok(await vehicles.RegisterAsync(
                new VehicleForm(categoryId, Plate(), "Renault", "Clio", 2025, "Gris", Transmission.Manual, FuelType.Petrol, 5, 1, 2, new DateOnly(2025, 1, 15), new DateOnly(2025, 1, 10), null),
                15_000));
            foreach (var type in VehicleCompliance.MandatoryTypes)
            {
                Ok(await vehicles.RecordDocumentAsync(vehicleId, new DocumentForm(type, $"{type}-{Guid.NewGuid():N}"[..20], null, new DateOnly(2030, 12, 31), null, null, null)));
            }
        }

        await using (var scope = Scope(host, manager))
        {
            var plans = scope.ServiceProvider.GetRequiredService<RatePlanAdministrationService>();
            var draftId = Ok(await plans.CreateDraftAsync(new CreateDraftRequest(agencyId, null, "Grille remise")));
            var detail = Ok(await plans.UpdatePricesAsync(draftId, 0, [new CategoryPriceDefinition(categoryId, RatePeriodKey.Base, Money.Tnd(100m))]));
            detail = Ok(await plans.UpdateTiersAsync(draftId, detail.Plan.Revision, [new(1, 0m)], []));
            Ok(await plans.PublishAsync(draftId, detail.Plan.Revision));
        }

        var agent = Actor(Roles.Agent);
        var lastName = $"Remise{Letters(8)}";
        Guid driverId;
        await using (var scope = Scope(host, agent))
        {
            var registration = scope.ServiceProvider.GetRequiredService<ICustomerRegistration>();
            var today = TunisTime.Today(TimeProvider.System);
            driverId = Ok(await registration.FindOrCreatePersonAsync(new PersonRegistrationRequest(
                "Sami",
                lastName,
                "TN",
                new DateOnly(1988, 3, 9),
                RandomNumberGenerator.GetInt32(20_000_000, 100_000_000).ToString(CultureInfo.InvariantCulture),
                $"conducteur-{Guid.NewGuid():N}@tests.local",
                Document: new IdentityDocumentInput(IdentityDocumentTypeInfo.NationalId, RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture), "TN", today.AddYears(-5))))).PersonId;
            Ok(await registration.AddOrReplaceDocumentAsync(
                driverId,
                new IdentityDocumentInput(IdentityDocumentTypeInfo.DrivingLicense, $"{RandomNumberGenerator.GetInt32(10, 99)}/{RandomNumberGenerator.GetInt32(100_000, 999_999)}", "TN", today.AddYears(-10))));
        }

        Guid bookingId;
        await using (var scope = Scope(host, agent))
        {
            var pickup = DateTimeOffset.UtcNow.AddMinutes(-10);
            bookingId = Ok(await scope.ServiceProvider.GetRequiredService<QuickBookingService>().CreateAsync(new QuickBookingRequest(
                agencyId, SalesChannel.Phone, categoryId, officeId, officeId, pickup, pickup.AddDays(1), driverId, QuickBookingOutcome.Confirmed))).BookingId;
            Ok(await scope.ServiceProvider.GetRequiredService<VehicleAssignmentService>().AssignAsync(bookingId, vehicleId));
        }

        await using var read = Scope(host, null);
        var booking = await read.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Booking>().AsNoTracking().SingleAsync(b => b.Id == bookingId);
        return new HandoverE2EFile(agencyId, bookingId, booking.Number, driverId, $"Sami {lastName}", vehicleId, booking.TotalInclVat, booking.Deposit);
    }

    /// <summary>Pays the file at the card terminal (its deposit too, when the category asks for one).</summary>
    public static async Task SettleAsync(E2EHost host, HandoverE2EFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        await PayAsync(host, file, file.Total.Amount);
        if (file.Deposit.IsPositive)
        {
            await using var scope = Scope(host, Actor(Roles.Agent));
            Ok(await scope.ServiceProvider.GetRequiredService<IDeposits>().TakeAsync(
                new DepositRequest(file.BookingId, file.AgencyId, DepositModeInfo.CardImprint, file.Deposit.Amount, "AUT-E2E")));
        }
    }

    /// <summary>Money received at the card terminal for the file, as the payments panel records it.</summary>
    public static async Task PayAsync(E2EHost host, HandoverE2EFile file, decimal amount)
    {
        ArgumentNullException.ThrowIfNull(file);
        await using var scope = Scope(host, Actor(Roles.Agent));
        Ok(await scope.ServiceProvider.GetRequiredService<ICounterPayments>().RecordPaymentAsync(
            new CounterPaymentRequest(file.BookingId, file.AgencyId, PaymentMethodInfo.CardTerminal, amount, "TPE-E2E")));
    }

    /// <summary>
    /// A departure done through the services (15 100 km, full tank, the eight photos, both signatures), the file paid; the
    /// vehicle left at <paramref name="pickedUpAt"/> (now by default).
    /// </summary>
    public static async Task DepartAsync(E2EHost host, HandoverE2EFile file, DateTimeOffset? pickedUpAt = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        var agent = Actor(Roles.Agent);
        Guid inspectionId;
        await using (var scope = Scope(host, agent))
        {
            inspectionId = Ok(await scope.ServiceProvider.GetRequiredService<DepartureService>().StartAsync(file.BookingId));
        }

        await using (var scope = Scope(host, agent))
        {
            Ok(await scope.ServiceProvider.GetRequiredService<DepartureService>().ConfirmVehicleAsync(file.BookingId));
        }

        await using (var scope = Scope(host, agent))
        {
            var inspections = scope.ServiceProvider.GetRequiredService<HandoverInspectionService>();
            Ok(await inspections.RecordOriginalsAsync(inspectionId, [new DriverOriginals(file.DriverId, true, true)]));
            Ok(await inspections.RecordReadingsAsync(inspectionId, new ReadingsInput(15_100, 8, true, true, true, true, 2, 0, false, true)));
        }

        foreach (var slot in PhotoSlots.Guided)
        {
            await using var scope = Scope(host, agent);
            var photo = Png(320, 240, slot.GetHashCode());
            Ok(await scope.ServiceProvider.GetRequiredService<HandoverInspectionService>().UploadPhotoAsync(inspectionId, slot, new MemoryStream(photo), new MemoryStream(photo)));
        }

        foreach (var (role, name) in new[] { (SignatureRole.Customer, file.DriverName), (SignatureRole.Agent, "Agent E2E") })
        {
            await using var scope = Scope(host, agent);
            Ok(await scope.ServiceProvider.GetRequiredService<HandoverInspectionService>().SignAsync(inspectionId, role, name, new MemoryStream(Png(200, 80, 7))));
        }

        await SettleAsync(host, file);
        await using (var scope = Scope(host, agent))
        {
            Ok(await scope.ServiceProvider.GetRequiredService<DepartureService>().FinishAsync(new FinishDepartureRequest(file.BookingId, pickedUpAt)));
        }
    }

    public static async Task<BookingStatus> StatusAsync(E2EHost host, Guid bookingId)
    {
        await using var scope = Scope(host, null);
        return await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Booking>().Where(b => b.Id == bookingId).Select(b => b.Status).SingleAsync();
    }

    /// <summary>A PNG image of <paramref name="width"/> × <paramref name="height"/> (a colour gradient), as a camera would give.</summary>
    public static byte[] Png(int width, int height, int seed = 0)
    {
        var raw = new byte[(height * ((width * 3) + 1))];
        var offset = 0;
        for (var y = 0; y < height; y++)
        {
            raw[offset++] = 0;
            for (var x = 0; x < width; x++)
            {
                raw[offset++] = (byte)((x * 255 / Math.Max(1, width - 1)) ^ seed);
                raw[offset++] = (byte)(y * 255 / Math.Max(1, height - 1));
                raw[offset++] = (byte)(128 + seed);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32([.. typeBytes, .. data]));
        png.Write(number);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static AsyncServiceScope Scope(E2EHost host, UserContextSnapshot? actor)
    {
        var scope = host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(actor ?? UserContextSnapshot.System("system:e2e"));
        return scope;
    }

    private static string Plate() => $"{100 + (Interlocked.Increment(ref _plateSequence) % 900)} TU {Random.Shared.Next(1000, 9999)}";

    private static string Letters(int count) => new([.. Enumerable.Range(0, count).Select(_ => (char)('a' + RandomNumberGenerator.GetInt32(26)))]);

    private static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    private static void Ok(Result result) => result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
}
