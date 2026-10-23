using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.E2E.Tests.Support;

/// <summary>
/// Rental files for the browser tests that record money (ADR-0067: a payment, a deposit or a payment link only points to
/// a file of its agency): a confirmed file of the main demo agency written directly — its office, a demo category and a
/// demo customer as driver and payer, but no quote nor vehicle, without its domain events (no notification, no invoice) — picked up
/// in 2099 and numbered in 2099, so that it takes no car of the agency from the other tests. The file appears in the
/// agency's lists like any other: its page shows its customer.
/// </summary>
internal static class E2EBookings
{
    private const string Category = "ECMR";
    private const string DriverEmail = "mohamed.gharbi@example.com";

    private static long _sequence = Random.Shared.Next(1, 100_000) * 1_000L;

    /// <summary>A confirmed file of the main demo agency worth <paramref name="total"/> (incl. VAT).</summary>
    public static async Task<Guid> NewOfDemoAgencyAsync(E2EHost host, decimal total = 300m)
    {
        ArgumentNullException.ThrowIfNull(host);
        Guid agencyId;
        Guid officeId;
        Guid categoryId;
        Guid driverId;
        Guid payerId;
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:e2e"));
            agencyId = (await scope.ServiceProvider.GetRequiredService<IReferentialQueries>().GetAgenciesAsync()).Single(a => a.Name == DemoUsers.DemoAgencyName).Id;
        }

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:e2e", agencyId: agencyId));
            officeId = (await scope.ServiceProvider.GetRequiredService<IReferentialQueries>().GetLocationsAsync(agencyId)).Single(l => l.Code == "AGC").Id;
            categoryId = (await scope.ServiceProvider.GetRequiredService<IFleetQueries>().FindCategoryByCodeAsync(agencyId, Category)).ShouldNotBeNull().Id;
            driverId = (await scope.ServiceProvider.GetRequiredService<ICustomersQueries>().SearchPersonsAsync(DriverEmail, 5)).Single(p => p.Email == DriverEmail).Id;
            var payer = await scope.ServiceProvider.GetRequiredService<ICustomerRegistration>().GetOrCreateIndividualCustomerAsync(driverId);
            payer.IsSuccess.ShouldBeTrue(payer.IsFailure ? payer.Error.ToString() : null);
            payerId = payer.Value;
        }

        var pickup = new DateTimeOffset(2099, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var booking = Booking.CreateConfirmed(
            Guid.CreateVersion7(),
            new BookingDetails(agencyId, SalesChannel.Counter, categoryId, officeId, officeId, pickup, pickup.AddDays(3), new BookingParties(driverId, null, payerId)),
            DocumentNumber.Create(DocumentType.Booking, 2099, Interlocked.Increment(ref _sequence)).Value,
            new BookingQuoteReference(Guid.CreateVersion7(), 1, Money.Tnd(total), Money.Tnd(500m)),
            BookingActor.System("system:e2e"),
            DateTimeOffset.UtcNow).Value;
        booking.DequeueDomainEvents();

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:e2e", agencyId: agencyId));
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Booking>().Add(booking);
            await db.SaveChangesAsync();
        }

        return booking.Id;
    }
}
