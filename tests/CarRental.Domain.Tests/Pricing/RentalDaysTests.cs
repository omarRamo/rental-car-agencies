using CarRental.Domain.Common;
using CarRental.Domain.Pricing;

namespace CarRental.Domain.Tests.Pricing;

/// <summary>Day count: 24-hour units from the pick-up time, one more day beyond the late tolerance, at least one day.</summary>
public sealed class RentalDaysTests
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(59);

    [Theory]
    [InlineData(35, 1)] // shorter than a day: the minimum of one day
    [InlineData(24 * 60, 1)]
    [InlineData((24 * 60) + 35, 1)] // within the tolerance: same count as the 35-minute rental (found by FsCheck)
    [InlineData((24 * 60) + 59, 1)]
    [InlineData((24 * 60) + 60, 2)] // beyond the tolerance: one more day
    [InlineData(5 * 24 * 60, 5)] // SPEC §7 reference example
    [InlineData((5 * 24 * 60) + 59, 5)] // return at 10:59
    [InlineData((5 * 24 * 60) + 60, 6)] // return at 11:00
    public void Counts_24_hour_units_with_the_late_tolerance(int minutes, int expectedDays)
    {
        var pickup = new DateTimeOffset(2027, 6, 28, 9, 0, 0, TimeSpan.Zero);
        var period = Period.Create(pickup, pickup.AddMinutes(minutes)).Value;

        RentalDays.Count(period, Tolerance).ShouldBe(expectedDays);
    }
}
