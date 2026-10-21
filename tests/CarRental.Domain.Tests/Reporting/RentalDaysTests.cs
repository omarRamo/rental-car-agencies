using CarRental.Domain.Common;
using CarRental.Domain.Reporting;

namespace CarRental.Domain.Tests.Reporting;

/// <summary>Rented days are the 24-hour units of the quote, dated by the Tunis date of their first instant (ADR-0045).</summary>
public sealed class RentalDaysTests
{
    [Fact]
    public void The_reference_rental_of_five_days_covers_the_pick_up_day_but_not_the_return_day()
    {
        var pickup = TunisTime.FromLocal(new DateOnly(2027, 6, 28), new TimeOnly(10, 0));

        var dates = RentalDays.DatesOf(pickup, 5);

        dates.ShouldBe([new(2027, 6, 28), new(2027, 6, 29), new(2027, 6, 30), new(2027, 7, 1), new(2027, 7, 2)]);
    }

    [Fact]
    public void A_unit_starts_every_twenty_four_hours_from_the_planned_pick_up()
    {
        var pickup = TunisTime.FromLocal(new DateOnly(2026, 10, 3), new TimeOnly(23, 30));

        RentalDays.StartOf(pickup, 0).ShouldBe(pickup.ToUniversalTime());
        RentalDays.StartOf(pickup, 2).ShouldBe(pickup.ToUniversalTime().AddDays(2));
        RentalDays.DateOf(pickup, 0).ShouldBe(new DateOnly(2026, 10, 3));
        RentalDays.DateOf(pickup, 1).ShouldBe(new DateOnly(2026, 10, 4));
    }

    [Fact]
    public void A_late_return_billed_one_more_day_makes_the_return_day_a_rented_day()
    {
        // 10:00 → 11:30 two days later: 2 days and 90 minutes, beyond the 59-minute tolerance, hence 3 priced days.
        var pickup = TunisTime.FromLocal(new DateOnly(2026, 10, 1), new TimeOnly(10, 0));

        RentalDays.DatesOf(pickup, 3)[^1].ShouldBe(new DateOnly(2026, 10, 3));
    }

    [Fact]
    public void A_rental_has_at_least_one_unit_and_units_are_counted_from_zero()
    {
        var pickup = TunisTime.FromLocal(new DateOnly(2026, 10, 1), new TimeOnly(10, 0));

        Should.Throw<ArgumentOutOfRangeException>(() => RentalDays.DatesOf(pickup, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => RentalDays.StartOf(pickup, -1));
    }
}
