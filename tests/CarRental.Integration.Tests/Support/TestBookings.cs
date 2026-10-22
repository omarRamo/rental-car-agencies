using CarRental.Application.Common.Persistence;
using CarRental.Domain.Bookings;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Support;

/// <summary>
/// Rental files for the tests that record money (ADR-0067: a payment, a deposit or a payment link only points to a file
/// of its agency): a confirmed file of the agency written directly — no quote, vehicle nor person behind its ids, without
/// its domain events (no notification, no invoice) — picked up in 2099, numbered in 2099.
/// </summary>
internal static class TestBookings
{
    private static long _sequence = Random.Shared.Next(1, 100_000) * 1_000L;

    /// <summary>A confirmed file of <paramref name="agencyId"/> worth <paramref name="total"/> (incl. VAT).</summary>
    public static async Task<Guid> NewAsync(IntegrationHost host, Guid agencyId, CancellationToken cancellationToken, decimal total = 300m)
    {
        ArgumentNullException.ThrowIfNull(host);
        var pickup = new DateTimeOffset(2099, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var booking = Booking.CreateConfirmed(
            Guid.CreateVersion7(),
            new BookingDetails(agencyId, SalesChannel.Counter, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), pickup, pickup.AddDays(3), new BookingParties(Guid.CreateVersion7(), null, Guid.CreateVersion7())),
            DocumentNumber.Create(DocumentType.Booking, 2099, Interlocked.Increment(ref _sequence)).Value,
            new BookingQuoteReference(Guid.CreateVersion7(), 1, Money.Tnd(total), Money.Tnd(500m)),
            BookingActor.System("system:tests"),
            DateTimeOffset.UtcNow).Value;
        booking.DequeueDomainEvents();

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        db.Set<Booking>().Add(booking);
        await db.SaveChangesAsync(cancellationToken);
        return booking.Id;
    }
}
