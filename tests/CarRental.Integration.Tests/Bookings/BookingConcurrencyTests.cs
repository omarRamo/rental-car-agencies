using System.Globalization;
using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Bookings.BookingWorld;

namespace CarRental.Integration.Tests.Bookings;

/// <summary>
/// Zero overbooking on SQL Server (ADR-0006, ADR-0025): the last vehicle goes to one writer only, whatever the channel,
/// and the readable numbers stay gapless under concurrency and replays.
/// </summary>
public sealed class BookingConcurrencyTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_simultaneous_requests_for_the_last_vehicle_of_a_category_one_succeeds()
    {
        var agency = await CreateAsync(host, Ct, ecmrVehicles: 1);
        var phoneDriver = await NewDriverAsync(host, agency.AgencyId, Ct);
        var webDriver = await NewDriverAsync(host, agency.AgencyId, Ct);
        var pickup = InDays(40);

        var phone = RunAsync<QuickBookingService, Result<QuickBookingResult>>(
            host,
            Agent,
            s => s.CreateAsync(Quick(agency, phoneDriver, pickup, pickup.AddDays(3)), Ct));
        var web = RunAsync<IBookingRequests, Result<BookingRequestResult>>(
            host,
            UserContextSnapshot.System("system:public-funnel"),
            s => s.SubmitRequestAsync(
                new BookingRequestDraft(SalesChannel.Web, Price: new PriceRequest(agency.AgencyId, agency.Ecmr, agency.OfficeId, agency.OfficeId, pickup.AddDays(1), pickup.AddDays(4), SalesChannel.Web)),
                new BookingRequestParties(webDriver),
                Ct));
        await Task.WhenAll(phone, web);

        Result[] outcomes = [await phone, await web];
        outcomes.Count(o => o.IsSuccess).ShouldBe(1, string.Join(", ", outcomes.Select(o => o.ToString())));
        outcomes.Single(o => o.IsFailure).Error.ShouldBe(AvailabilityErrors.NotAvailable);
        (await OccupyingAsync(agency.Ecmr)).ShouldBe(1);
    }

    [Fact]
    public async Task Many_concurrent_requests_never_exceed_the_vehicles_of_the_category()
    {
        var agency = await CreateAsync(host, Ct, ecmrVehicles: 3);
        var pickup = InDays(50);
        var drivers = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            drivers.Add(await NewDriverAsync(host, agency.AgencyId, Ct));
        }

        var outcomes = await Task.WhenAll(drivers.Select((driver, i) => RunAsync<QuickBookingService, Result<QuickBookingResult>>(
            host,
            Agent,
            s => s.CreateAsync(Quick(agency, driver, pickup.AddHours(i), pickup.AddDays(2).AddHours(i)), Ct))));

        outcomes.Count(o => o.IsSuccess).ShouldBe(3);
        outcomes.Where(o => o.IsFailure).ShouldAllBe(o => o.Error == AvailabilityErrors.NotAvailable);
        (await OccupyingAsync(agency.Ecmr)).ShouldBe(3);
    }

    [Fact]
    public async Task Web_requests_and_phone_bookings_racing_for_the_last_vehicles_never_deadlock()
    {
        var agency = await CreateAsync(host, Ct, ecmrVehicles: 2);
        var pickup = InDays(55);
        var drivers = new List<Guid>();
        for (var i = 0; i < 40; i++)
        {
            drivers.Add(await NewDriverAsync(host, agency.AgencyId, Ct));
        }

        // A request draws its number after the availability lock, like a phone booking: neither waits on the other.
        var outcomes = await Task.WhenAll(drivers.Select((driver, i) => i % 2 == 0
            ? RunAsync<QuickBookingService, Result>(
                host,
                Agent,
                async s => await s.CreateAsync(Quick(agency, driver, pickup.AddHours(i % 4), pickup.AddDays(2)), Ct))
            : RunAsync<IBookingRequests, Result>(
                host,
                UserContextSnapshot.System("system:public-funnel"),
                async s => await s.SubmitRequestAsync(
                    new BookingRequestDraft(SalesChannel.Web, Price: new PriceRequest(agency.AgencyId, agency.Ecmr, agency.OfficeId, agency.OfficeId, pickup.AddHours(i % 4), pickup.AddDays(2), SalesChannel.Web)),
                    new BookingRequestParties(driver),
                    Ct))));

        outcomes.Count(o => o.IsSuccess).ShouldBe(2, string.Join(", ", outcomes.Select(o => o.ToString())));
        outcomes.Where(o => o.IsFailure).ShouldAllBe(o => o.Error == AvailabilityErrors.NotAvailable, "never availability.lock_deadlock nor lock_timeout");
        (await OccupyingAsync(agency.Ecmr)).ShouldBe(2);
    }

    [Fact]
    public async Task Committed_booking_numbers_of_the_year_are_gapless_even_when_an_operation_is_replayed()
    {
        var agency = await CreateAsync(host, Ct, ecmrVehicles: 2);
        var driver = await NewDriverAsync(host, agency.AgencyId, Ct);
        var attempts = 0;
        string? number = null;

        await using (var scope = host.CreateScope(Agent))
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var bookings = scope.ServiceProvider.GetRequiredService<QuickBookingService>();
            var outcome = await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    attempts++;
                    var created = await bookings.CreateAsync(Quick(agency, driver, InDays(60), InDays(62)), ct);
                    if (created.IsFailure)
                    {
                        return created.Error;
                    }

                    number = created.Value.Number;
                    if (attempts == 1)
                    {
                        // A transient failure after the number was taken: the whole operation is replayed.
                        throw new TimeoutException("Simulated transient failure.");
                    }

                    return Result.Success();
                },
                cancellationToken: Ct);
            outcome.IsSuccess.ShouldBeTrue(outcome.ToString());
        }

        attempts.ShouldBe(2);
        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        (await db.Set<Booking>().CountAsync(b => b.MainDriverId == driver, Ct)).ShouldBe(1, "the first attempt was rolled back");
        var year = TunisTime.Today(TimeProvider.System).Year;
        var prefix = string.Create(CultureInfo.InvariantCulture, $"LOC-{year}-");
        // The numbers are drawn per agency (ADR-0067): the test agency numbers its own files only.
        var sequences = (await db.Set<Booking>().AsNoTracking().Where(b => b.AgencyId == agency.AgencyId && b.Number.StartsWith(prefix)).Select(b => b.Number).ToListAsync(Ct))
            .Select(n => long.Parse(n[prefix.Length..], CultureInfo.InvariantCulture))
            .Order()
            .ToList();
        sequences.ShouldBe(Enumerable.Range(1, sequences.Count).Select(i => (long)i), "committed numbers never leave a gap within an agency");
        sequences.ShouldContain(long.Parse(number![prefix.Length..], CultureInfo.InvariantCulture));
    }

    private async Task<int> OccupyingAsync(Guid categoryId)
    {
        await using var scope = host.CreateScope();
        var statuses = BookingStatusRules.Occupying.ToArray();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Booking>()
            .CountAsync(b => b.CategoryId == categoryId && statuses.Contains(b.Status), Ct);
    }
}
