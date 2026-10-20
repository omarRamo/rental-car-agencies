using System.Diagnostics;
using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Application.Tests.Bookings.Availability.AvailabilityWorld;

namespace CarRental.Application.Tests.Bookings.Availability;

/// <summary>PROMPT §8: cached reads for the bot and the channels answer in less than 100 ms.</summary>
public sealed class AvailabilityPerformanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cached_reads_answer_well_under_100_ms_for_30_vehicles_and_2000_bookings_a_year()
    {
        // 6 categories of 5 vehicles; 2,200 bookings of 1 to 10 days over the 400 cached days (2,000 a year).
        var world = new AvailabilityWorld();
        var random = new Random(20_271_001);
        var categories = new[] { "MCMR", "ECMR", "EDAR", "CDMR", "IFMR", "SFAR" }
            .Select((code, index) => world.Category(code, vehicles: 5, displayOrder: index))
            .ToArray();
        for (var i = 0; i < 2_200; i++)
        {
            var start = random.Next(-24, 400 * 24);
            world.Book(categories[random.Next(categories.Length)], P(start, start + (random.Next(1, 11) * 24)));
        }

        await using var services = world.Services();
        await using var scope = services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();

        // Warm-up: loads every snapshot once and compiles the read path.
        await queries.GetAvailableCategoriesAsync(world.AgencyId, P(24, 72), SalesChannel.Web, Ct);
        await queries.GetCategoryAvailabilityAsync(categories[0].Id, P(24, 72), SalesChannel.BookingCom, cancellationToken: Ct);

        var timings = new List<TimeSpan>();
        for (var i = 0; i < 300; i++)
        {
            var start = random.Next(0, 380 * 24);
            var rental = P(start, start + random.Next(24, 15 * 24));
            var stopwatch = Stopwatch.StartNew();
            await queries.GetAvailableCategoriesAsync(world.AgencyId, rental, (SalesChannel)(1 + (i % 5)), Ct);
            await queries.GetCategoryAvailabilityAsync(categories[i % categories.Length].Id, rental, SalesChannel.BookingCom, cancellationToken: Ct);
            timings.Add(stopwatch.Elapsed);
        }

        var sorted = timings.Order().ToList();
        var p95 = sorted[(int)(sorted.Count * 0.95)];
        TestContext.Current.TestOutputHelper?.WriteLine($"median {sorted[sorted.Count / 2].TotalMilliseconds:F3} ms, p95 {p95.TotalMilliseconds:F3} ms, max {sorted[^1].TotalMilliseconds:F3} ms");

        services.GetRequiredService<AvailabilityCache>().SnapshotLoads.ShouldBe(categories.Length);
        p95.ShouldBeLessThan(TimeSpan.FromMilliseconds(100));
        TimeSpan.FromTicks((long)timings.Average(t => t.Ticks)).ShouldBeLessThan(TimeSpan.FromMilliseconds(25));
    }
}
