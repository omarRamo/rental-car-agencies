using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using static CarRental.Application.Tests.Bookings.Availability.AvailabilityWorld;

namespace CarRental.Application.Tests.Bookings.Availability;

/// <summary>
/// Random write sequences (bookings, amendments, extensions of rentals in progress, cancellations, several channels)
/// through the real guard — lock, fresh reads, clipping to now, amended booking left out: from now on, the accepted
/// bookings never exceed the vehicles in service (invariant 2). Each seed replays the same sequence.
/// </summary>
public sealed class AvailabilityGuardSequenceTests
{
    private const int Horizon = 240;

    private static readonly SalesChannel[] Channels = [SalesChannel.Web, SalesChannel.Counter, SalesChannel.BookingCom];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1, 60)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Writes_accepted_by_the_guard_never_exceed_the_vehicles_in_service_from_now_on(int seed)
    {
        var random = new Random(seed);
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 0);
        var vehicleCount = random.Next(1, 5);
        for (var i = 0; i < vehicleCount; i++)
        {
            world.Fleet.AddVehicle(category, $"{seed:D3} TU {i:D4}", RandomWindows(random));
        }

        var buffer = random.Next(0, 4);
        world.Settings.BufferByAgency[world.AgencyId] = TimeSpan.FromHours(buffer);
        world.Channels.Set(category.Id, SalesChannel.Web, new ChannelRestrictionsInfo([], [RandomSpan(random, 48)]));
        world.Channels.Set(category.Id, SalesChannel.BookingCom, new ChannelRestrictionsInfo([new ReservedVehiclesInfo(RandomSpan(random, 120), 1)], []));
        var guard = world.Guard();
        var accepted = new List<Guid>();

        for (var step = 0; step < 50; step++)
        {
            var roll = random.Next(100);
            var channel = Channels[random.Next(Channels.Length)];
            BookingOccupancy booking;
            if (roll < 60 || accepted.Count == 0)
            {
                booking = Planned(Guid.CreateVersion7(), RandomSpan(random, 72));
            }
            else if (roll < 80)
            {
                booking = Planned(accepted[random.Next(accepted.Count)], RandomSpan(random, 72));
            }
            else if (roll < 90)
            {
                // A rental that left in the past is extended (or shortened) while in progress.
                var pickup = random.Next(-48, 0);
                booking = new BookingOccupancy(accepted[random.Next(accepted.Count)], OccupancyKind.InProgress, P(pickup, random.Next(1, 120)), At(pickup));
            }
            else
            {
                var cancelled = accepted[random.Next(accepted.Count)];
                world.Occupancy.Upsert(category.Id, new BookingOccupancy(cancelled, OccupancyKind.None, P(0, 1)));
                accepted.Remove(cancelled);
                continue;
            }

            var request = new AvailabilityRequest(category.Id, channel, booking);
            var written = await world.UnitOfWork.ExecuteAsync(
                async ct =>
                {
                    var available = await guard.EnsureAvailableAsync(request, cancellationToken: ct);
                    if (available.IsSuccess)
                    {
                        world.Occupancy.Upsert(category.Id, booking);
                    }

                    return available;
                },
                cancellationToken: Ct);

            if (written.IsSuccess && !accepted.Contains(booking.BookingId))
            {
                accepted.Add(booking.BookingId);
            }
            else if (written.IsFailure)
            {
                written.Error.ShouldBe(AvailabilityErrors.NotAvailable);
            }
        }

        var basis = new OccupancyBasis(Now, TimeSpan.FromHours(buffer));
        var intervals = world.Occupancy.BookingsOf(category.Id).Select(basis.IntervalOf).OfType<OccupancyInterval>().ToList();
        var vehicles = await world.Fleet.GetAvailabilityAsync(category.Id, Always, Ct);
        for (var hour = 0; hour < Horizon + 200; hour++)
        {
            var instant = At(hour);
            var occupied = intervals.Count(i => i.Period.Contains(instant));
            var inService = vehicles.Count(v => v.Windows.Any(w => w.Contains(instant)));
            occupied.ShouldBeLessThanOrEqualTo(inService, $"seed {seed}, hour {hour}");
        }
    }

    private static Period RandomSpan(Random random, int maxHours)
    {
        var start = random.Next(-12, Horizon);
        return P(start, start + random.Next(1, maxHours + 1));
    }

    /// <summary>Always in service, or with a maintenance block, or sold during the horizon.</summary>
    private static Period[] RandomWindows(Random random)
    {
        var cut = random.Next(0, Horizon);
        return random.Next(3) switch
        {
            0 => [Always],
            1 => [P(-24 * 365, cut), P(cut + random.Next(1, 48), 24 * 365)],
            _ => [P(-24 * 365, cut)],
        };
    }
}
