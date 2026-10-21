using System.Globalization;
using System.Security.Cryptography;
using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Handover;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Customers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Bookings.BookingWorld;

namespace CarRental.Integration.Tests.Bookings.Handover;

/// <summary>A confirmed rental file of a test agency of its own, with its vehicle assigned and a driver with an e-mail.</summary>
internal sealed record HandoverFile(BookingAgency Agency, Guid BookingId, string Number, Guid DriverId, string DriverEmail, Guid VehicleId, Money Total, Money Deposit);

/// <summary>The steps of a departure and a return through the services, as the tablet screens call them.</summary>
internal static class HandoverWorld
{
    /// <summary>A JPEG header: the server checks the real type of a photo, never decodes it.</summary>
    public static byte[] Jpeg { get; } = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00];

    public static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    public static void Ok(Result result) => result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);

    /// <summary>
    /// A confirmed file for one day from now (vehicle assigned); with <paramref name="shiftedBack"/>, its period is moved
    /// that far into the past (a rental that started earlier, to test a late return on the real clock).
    /// </summary>
    public static async Task<HandoverFile> ConfirmedAsync(IntegrationHost host, CancellationToken cancellationToken, TimeSpan? shiftedBack = null)
    {
        var agency = await CreateAsync(host, cancellationToken, ecmrVehicles: 1);
        var (driverId, email) = await NewDriverAsync(host, cancellationToken);
        var pickup = DateTimeOffset.UtcNow.AddMinutes(-10);
        var created = await BookingWorld.CreateAsync(host, Quick(agency, driverId, pickup, pickup.AddDays(1)), cancellationToken);
        Ok(await RunAsync<VehicleAssignmentService, Result>(host, Agent, s => s.AssignAsync(created.BookingId, agency.EcmrVehicles[0], cancellationToken: cancellationToken)));
        if (shiftedBack is { } shift)
        {
            await ShiftAsync(host, created.BookingId, shift, cancellationToken);
        }

        await using var scope = host.CreateScope();
        var booking = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<CarRental.Domain.Bookings.Booking>()
            .AsNoTracking()
            .SingleAsync(b => b.Id == created.BookingId, cancellationToken);
        return new HandoverFile(agency, booking.Id, booking.Number, driverId, email, agency.EcmrVehicles[0], booking.TotalInclVat, booking.Deposit);
    }

    /// <summary>Pays the whole file at the card terminal and takes the deposit as a card imprint (as the agent).</summary>
    public static async Task<Guid> SettleAsync(IntegrationHost host, UserContextSnapshot agent, HandoverFile file, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope(agent);
        Ok(await scope.ServiceProvider.GetRequiredService<ICounterPayments>().RecordPaymentAsync(
            new CounterPaymentRequest(file.BookingId, file.Agency.AgencyId, PaymentMethodInfo.CardTerminal, file.Total.Amount, $"TPE-{Guid.NewGuid():N}"[..16]),
            cancellationToken));
        return Ok(await scope.ServiceProvider.GetRequiredService<IDeposits>().TakeAsync(
            new DepositRequest(file.BookingId, file.Agency.AgencyId, DepositModeInfo.CardImprint, file.Deposit.Amount, $"AUT-{Guid.NewGuid():N}"[..16]),
            cancellationToken));
    }

    /// <summary>The departure inspection ready to finish: vehicle confirmed, originals, readings, the eight photos, both signatures.</summary>
    public static async Task<Guid> InspectDepartureAsync(IntegrationHost host, UserContextSnapshot agent, HandoverFile file, CancellationToken cancellationToken)
    {
        var inspectionId = Ok(await RunAsync<DepartureService, Result<Guid>>(host, agent, s => s.StartAsync(file.BookingId, cancellationToken)));
        Ok(await RunAsync<DepartureService, Result>(host, agent, s => s.ConfirmVehicleAsync(file.BookingId, cancellationToken)));
        Ok(await RunAsync<HandoverInspectionService, Result>(host, agent, s => s.RecordOriginalsAsync(inspectionId, [new DriverOriginals(file.DriverId, true, true)], cancellationToken)));
        Ok(await RunAsync<HandoverInspectionService, Result>(host, agent, s => s.RecordReadingsAsync(inspectionId, Readings(15_100, 8), cancellationToken)));
        foreach (var slot in PhotoSlots.Guided)
        {
            Ok(await RunAsync<HandoverInspectionService, Result<Guid>>(
                host,
                agent,
                s => s.UploadPhotoAsync(inspectionId, slot, new MemoryStream(Jpeg), new MemoryStream(Jpeg), cancellationToken: cancellationToken)));
        }

        await SignAsync(host, agent, inspectionId, cancellationToken);
        var preview = Ok(await RunAsync<DepartureService, Result<RentalContractDocument>>(host, agent, s => s.PreviewContractAsync(file.BookingId, cancellationToken: cancellationToken)));
        preview.IsPreview.ShouldBeTrue();
        preview.Number.ShouldBe(file.Number);
        return inspectionId;
    }

    public static async Task SignAsync(IntegrationHost host, UserContextSnapshot agent, Guid inspectionId, CancellationToken cancellationToken)
    {
        Ok(await RunAsync<HandoverInspectionService, Result>(host, agent, s => s.SignAsync(inspectionId, SignatureRole.Customer, "Client Tests", new MemoryStream(CustomerTestData.Png()), cancellationToken)));
        Ok(await RunAsync<HandoverInspectionService, Result>(host, agent, s => s.SignAsync(inspectionId, SignatureRole.Agent, "Agent Tests", new MemoryStream(CustomerTestData.Png()), cancellationToken)));
    }

    public static ReadingsInput Readings(int mileage, int fuel) => new(mileage, fuel, true, true, true, true, 2, 0, false, true);

    /// <summary>An eligible Tunisian driver with an e-mail address of his own (the contract is sent to it).</summary>
    private static async Task<(Guid PersonId, string Email)> NewDriverAsync(IntegrationHost host, CancellationToken cancellationToken)
    {
        var email = $"conducteur-{Guid.NewGuid():N}@tests.local";
        await using var scope = host.CreateScope(Agent);
        var registration = scope.ServiceProvider.GetRequiredService<ICustomerRegistration>();
        var today = TunisTime.Today(TimeProvider.System);
        var person = Ok(await registration.FindOrCreatePersonAsync(
            new PersonRegistrationRequest(
                "Sami",
                $"Ben{Letters(8)}",
                "TN",
                new DateOnly(1988, 3, 9),
                RandomNumberGenerator.GetInt32(20_000_000, 100_000_000).ToString(CultureInfo.InvariantCulture),
                email,
                Document: new IdentityDocumentInput(
                    IdentityDocumentTypeInfo.NationalId,
                    RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture),
                    "TN",
                    today.AddYears(-5))),
            cancellationToken));
        Ok(await registration.AddOrReplaceDocumentAsync(
            person.PersonId,
            new IdentityDocumentInput(IdentityDocumentTypeInfo.DrivingLicense, $"{RandomNumberGenerator.GetInt32(10, 99)}/{RandomNumberGenerator.GetInt32(100_000, 999_999)}", "TN", today.AddYears(-10)),
            cancellationToken: cancellationToken));
        return (person.PersonId, email);
    }

    private static string Letters(int count) =>
        string.Create(count, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = (char)('a' + RandomNumberGenerator.GetInt32(26));
            }
        });

    /// <summary>Moves the period of a file and of its quotes into the past (test data only: the domain refuses a past pick-up).</summary>
    private static async Task ShiftAsync(IntegrationHost host, Guid bookingId, TimeSpan shift, CancellationToken cancellationToken)
    {
        var minutes = (int)shift.TotalMinutes;
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE bookings.Bookings SET PickupAt = DATEADD(minute, -{minutes}, PickupAt), ReturnAt = DATEADD(minute, -{minutes}, ReturnAt) WHERE Id = {bookingId}",
            cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE bookings.Quotes SET PickupAt = DATEADD(minute, -{minutes}, PickupAt), ReturnAt = DATEADD(minute, -{minutes}, ReturnAt) WHERE BookingId = {bookingId}",
            cancellationToken);
    }
}
