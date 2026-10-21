using CarRental.Domain.Common;
using CarRental.Domain.Reporting;

namespace CarRental.Domain.Tests.Reporting;

/// <summary>Time within the opening hours of the agency ("heures ouvrées", SPEC §2).</summary>
public sealed class BusinessHoursTests
{
    private static readonly TimeOnly Opens = new(8, 0);
    private static readonly TimeOnly Closes = new(20, 0);
    private static readonly DateOnly Monday = new(2026, 10, 19);

    [Fact]
    public void A_wait_within_the_opening_hours_counts_entirely()
    {
        BusinessHours.Between(At(Monday, 9, 0), At(Monday, 10, 30), Opens, Closes).ShouldBe(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void The_night_does_not_count()
    {
        // Submitted at 19:30, decided at 08:45 the next morning: 30 minutes the evening, 45 minutes the morning.
        BusinessHours.Between(At(Monday, 19, 30), At(Monday.AddDays(1), 8, 45), Opens, Closes).ShouldBe(TimeSpan.FromMinutes(75));
    }

    [Fact]
    public void A_wait_outside_the_opening_hours_counts_nothing()
    {
        BusinessHours.Between(At(Monday, 21, 0), At(Monday.AddDays(1), 7, 0), Opens, Closes).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void A_wait_over_several_days_counts_every_opening()
    {
        BusinessHours.Between(At(Monday, 7, 0), At(Monday.AddDays(2), 9, 0), Opens, Closes).ShouldBe(TimeSpan.FromHours(25));
    }

    [Fact]
    public void An_empty_interval_or_invalid_hours_count_nothing()
    {
        BusinessHours.Between(At(Monday, 10, 0), At(Monday, 10, 0), Opens, Closes).ShouldBe(TimeSpan.Zero);
        BusinessHours.Between(At(Monday, 11, 0), At(Monday, 10, 0), Opens, Closes).ShouldBe(TimeSpan.Zero);
        BusinessHours.Between(At(Monday, 9, 0), At(Monday, 10, 0), Closes, Opens).ShouldBe(TimeSpan.Zero);
    }

    private static DateTimeOffset At(DateOnly day, int hour, int minute) => TunisTime.FromLocal(day, new TimeOnly(hour, minute));
}
