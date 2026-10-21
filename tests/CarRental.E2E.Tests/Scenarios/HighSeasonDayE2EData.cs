using System.Globalization;
using System.Security.Cryptography;
using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Administration;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Common;
using CarRental.E2E.Tests.Bookings.Handover;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.E2E.Tests.Scenarios;

/// <summary>
/// Arrangements of the high-season day in the browser, through the services: the demo agency sold out but two cars of
/// a category for the dates of the scenario, the walk-in of today paid at the counter, and the late rental of an agency
/// of its own (tests run in parallel on one database: dates, customers and agency belong to this scenario).
/// </summary>
internal static class HighSeasonDayE2EData
{
    /// <summary>The category sold out by the scenario: no other browser test books it.</summary>
    public const string Category = "MCMR";

    /// <summary>
    /// The dates of the scenario: no demo booking of the category then, no other browser test on those days, and the
    /// documents of the demo cars still valid (their insurance ends two to four months from the seeding).
    /// </summary>
    public const int DaysAhead = 30;

    private const string DemoAgencyName = "Agence Démo Tunis";

    private static readonly UserContextSnapshot Agent =
        UserContextSnapshot.ForUser(Guid.CreateVersion7(), $"agent-saison-{Guid.NewGuid():N}@tests.local", RolePresets.PermissionsOf(Roles.Agent));

    /// <summary>The demo agency (the one the public site and the back office serve), its office and the category of the scenario.</summary>
    public sealed record DemoAgency(Guid AgencyId, Guid OfficeId, Guid CategoryId);

    /// <summary>The walk-in of today: confirmed, its vehicle assigned, paid at the card terminal with a card imprint.</summary>
    public sealed record Walkin(Guid BookingId, string Number, Guid VehicleId, int Mileage);

    /// <summary>A rental of an agency of its own, gone yesterday morning for a day: one day late now.</summary>
    public sealed record Late(HandoverE2EFile File, string AgencyName, DateOnly PlannedReturnDay);

    /// <summary>
    /// The scenario plays on one Tunis day (the dashboard shows today's departures): started in its last ten minutes, it
    /// waits for the next one.
    /// </summary>
    public static async Task WaitForAWholeDayAsync()
    {
        var left = TunisTime.StartOfDay(TunisTime.Today(TimeProvider.System).AddDays(1)) - TimeProvider.System.GetUtcNow();
        if (left < TimeSpan.FromMinutes(10))
        {
            await Task.Delay(left + TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A local date and time for a <c>datetime-local</c> input and the search of the public site: 10:00 that day.</summary>
    public static string Input(DateOnly day) => $"{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}T10:00";

    /// <summary>A short unique suffix of letters only (names accept letters only).</summary>
    public static string Unique() =>
        new([.. Guid.NewGuid().ToString("N")[..8].Select(c => char.IsAsciiDigit(c) ? (char)('G' + (c - '0')) : char.ToUpperInvariant(c))]);

    public static string Letters(int count) => new([.. Enumerable.Range(0, count).Select(_ => (char)('a' + RandomNumberGenerator.GetInt32(26)))]);

    public static Task<DemoAgency> DemoAsync(E2EHost host) => RunAsync(host, async services =>
    {
        var referential = services.GetRequiredService<IReferentialQueries>();
        var agency = (await referential.GetAgenciesAsync()).Single(a => a.Name == DemoAgencyName);
        var office = (await referential.GetLocationsAsync(agency.Id)).Single(l => l.Code == "AGC");
        var category = (await services.GetRequiredService<IFleetQueries>().FindCategoryByCodeAsync(agency.Id, Category)).ShouldNotBeNull();
        return new DemoAgency(agency.Id, office.Id, category.Id);
    });

    /// <summary>The high season: every car of the category is sold for the dates of the scenario but two (phone bookings).</summary>
    public static async Task SellAllButTwoAsync(E2EHost host, DemoAgency demo, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(demo);
        var pickupAt = TunisTime.FromLocal(day, new TimeOnly(10, 0));
        var returnAt = TunisTime.FromLocal(day.AddDays(3), new TimeOnly(10, 0));
        var remaining = await RemainingAsync(host, demo.CategoryId, pickupAt, returnAt);
        remaining.ShouldBeGreaterThanOrEqualTo(2, $"the demo agency has {remaining} {Category} for the dates of the scenario");
        for (var sold = 0; sold < remaining - 2; sold++)
        {
            var driver = await NewDriverAsync(host);
            await RunAsync(host, async services => Ok(await services.GetRequiredService<QuickBookingService>().CreateAsync(new QuickBookingRequest(
                demo.AgencyId, SalesChannel.Phone, demo.CategoryId, demo.OfficeId, demo.OfficeId, pickupAt, returnAt, driver, QuickBookingOutcome.Confirmed))));
        }

        (await RemainingAsync(host, demo.CategoryId, pickupAt, returnAt)).ShouldBe(2);
    }

    /// <summary>
    /// A walk-in of today in a category (other than the scenario's) with a car free until tomorrow: confirmed at the
    /// counter, the free car assigned, the total paid at the card terminal and the deposit taken as a card imprint.
    /// </summary>
    public static async Task<Walkin> WalkInOfTodayAsync(E2EHost host, DemoAgency demo)
    {
        ArgumentNullException.ThrowIfNull(demo);
        var pickupAt = TimeProvider.System.GetUtcNow().AddMinutes(1);
        var returnAt = pickupAt.AddDays(1);
        var categories = await RunAsync(host, services => services.GetRequiredService<IFleetQueries>().GetCategoriesAsync(demo.AgencyId));
        var driver = await NewDriverAsync(host, withEmail: true);
        foreach (var category in categories.Where(c => c.AcrissCode != Category))
        {
            if (await RemainingAsync(host, category.Id, pickupAt, returnAt) == 0)
            {
                continue;
            }

            var booking = await RunAsync(host, async services => Ok(await services.GetRequiredService<QuickBookingService>().CreateAsync(new QuickBookingRequest(
                demo.AgencyId, SalesChannel.Counter, category.Id, demo.OfficeId, demo.OfficeId, pickupAt, returnAt, driver, QuickBookingOutcome.Confirmed))));
            var candidates = await RunAsync(host, async services => Ok(await services.GetRequiredService<VehicleAssignmentService>().GetCandidatesAsync(booking.BookingId)));
            if (candidates.FirstOrDefault(c => c.State == VehicleCandidateState.Free) is not { } free)
            {
                // A seat in the category but no single car free over the whole rental: try another category.
                await RunAsync(host, async services => Ok(await services.GetRequiredService<BookingWorkflowService>().CancelAsync(booking.BookingId, "Essai d'une autre catégorie")));
                continue;
            }

            await RunAsync(host, async services =>
            {
                Ok(await services.GetRequiredService<VehicleAssignmentService>().AssignAsync(booking.BookingId, free.VehicleId));
                return true;
            });
            var file = await RunAsync(host, async services => (await services.GetRequiredService<IBookingsQueries>().GetSummaryAsync(booking.BookingId)).ShouldNotBeNull());
            await RunAsync(host, async services =>
            {
                Ok(await services.GetRequiredService<ICounterPayments>().RecordPaymentAsync(
                    new CounterPaymentRequest(file.Id, demo.AgencyId, PaymentMethodInfo.CardTerminal, file.TotalInclVat.Amount, "TPE-SAISON")));
                if (file.Deposit.IsPositive)
                {
                    Ok(await services.GetRequiredService<IDeposits>().TakeAsync(
                        new DepositRequest(file.Id, demo.AgencyId, DepositModeInfo.CardImprint, file.Deposit.Amount, "AUT-SAISON")));
                }

                return true;
            });
            var vehicle = await RunAsync(host, async services => (await services.GetRequiredService<IFleetQueries>().GetVehicleAsync(free.VehicleId)).ShouldNotBeNull());
            return new Walkin(file.Id, file.Number, free.VehicleId, vehicle.Mileage);
        }

        throw new InvalidOperationException("No car of the demo agency is free from now to tomorrow.");
    }

    /// <summary>
    /// A rental of an agency of its own, handed over through the services, then moved two days back (test data only: the
    /// domain refuses a past pick-up): due back yesterday, one day late now.
    /// </summary>
    public static async Task<Late> LateRentalAsync(E2EHost host)
    {
        var file = await HandoverE2EData.ConfirmedAsync(host);
        await HandoverE2EData.DepartAsync(host, file);
        var returnAt = await RunAsync(host, async services =>
        {
            var db = services.GetRequiredService<CarRentalDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE bookings.Bookings SET PickupAt = DATEADD(day, -2, PickupAt), ReturnAt = DATEADD(day, -2, ReturnAt), ActualPickupAt = DATEADD(day, -2, ActualPickupAt) WHERE Id = {file.BookingId}");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE bookings.Quotes SET PickupAt = DATEADD(day, -2, PickupAt), ReturnAt = DATEADD(day, -2, ReturnAt) WHERE BookingId = {file.BookingId}");
            return await db.Set<Booking>().AsNoTracking().Where(b => b.Id == file.BookingId).Select(b => b.ReturnAt).SingleAsync();
        });
        var name = await RunAsync(host, async services => (await services.GetRequiredService<IReferentialQueries>().GetAgenciesAsync()).Single(a => a.Id == file.AgencyId).Name);
        return new Late(file, name, TunisTime.DateOf(returnAt));
    }

    public static Task<BookingStatus> StatusAsync(E2EHost host, string number) =>
        RunAsync(host, services => services.GetRequiredService<IAppDbContext>().Set<Booking>().Where(b => b.Number == number).Select(b => b.Status).SingleAsync());

    private static Task<int> RemainingAsync(E2EHost host, Guid categoryId, DateTimeOffset pickupAt, DateTimeOffset returnAt) =>
        RunAsync(host, async services =>
            (await services.GetRequiredService<IAvailabilityQueries>().GetCategoryAvailabilityAsync(categoryId, Period.Create(pickupAt, returnAt).Value, SalesChannel.Counter))
            .ShouldNotBeNull().Remaining);

    /// <summary>An eligible Tunisian driver of the scenario: CIN, a licence held for ten years (and an e-mail for the contract).</summary>
    private static async Task<Guid> NewDriverAsync(E2EHost host, bool withEmail = false)
    {
        var lastName = "Saison" + Letters(8);
        var today = TunisTime.Today(TimeProvider.System);
        return await RunAsync(host, async services =>
        {
            var registration = services.GetRequiredService<ICustomerRegistration>();
            var person = Ok(await registration.FindOrCreatePersonAsync(new PersonRegistrationRequest(
                "Sami",
                lastName,
                "TN",
                new DateOnly(1987, 5, 4),
                RandomNumberGenerator.GetInt32(20_000_000, 100_000_000).ToString(CultureInfo.InvariantCulture),
                withEmail ? $"{lastName.ToLowerInvariant()}@clients.test" : null,
                Document: new IdentityDocumentInput(IdentityDocumentTypeInfo.NationalId, RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture), "TN", today.AddYears(-5)))));
            Ok(await registration.AddOrReplaceDocumentAsync(
                person.PersonId,
                new IdentityDocumentInput(IdentityDocumentTypeInfo.DrivingLicense, $"{RandomNumberGenerator.GetInt32(10, 99)}/{RandomNumberGenerator.GetInt32(100_000, 999_999)}", "TN", today.AddYears(-10))));
            return person.PersonId;
        });
    }

    private static async Task<T> RunAsync<T>(E2EHost host, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(Agent);
        return await action(scope.ServiceProvider);
    }

    private static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    private static void Ok(Result result) => result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
}
