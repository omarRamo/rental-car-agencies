using CarRental.Domain.Common;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Domain.Tests.Common;

public sealed class TunisTimeTests
{
    [Fact]
    public void Zone_is_resolved_on_every_platform()
    {
        TunisTime.Zone.ShouldNotBeNull();
        TunisTime.Zone.BaseUtcOffset.ShouldBe(TimeSpan.FromHours(1));
    }

    [Theory]
    [InlineData(2027, 1, 15)]
    [InlineData(2027, 6, 28)]
    [InlineData(2027, 7, 3)]
    [InlineData(2027, 12, 31)]
    public void Tunis_is_utc_plus_one_all_year_round(int year, int month, int day)
    {
        var noonUtc = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero);

        var tunis = TunisTime.ToTunis(noonUtc);

        tunis.Offset.ShouldBe(TimeSpan.FromHours(1));
        tunis.Hour.ShouldBe(13);
        tunis.ShouldBe(noonUtc);
    }

    [Fact]
    public void FromLocal_interprets_a_wall_clock_time_in_tunis()
    {
        // Reference example: pick-up 28/06/2027 10:00 Africa/Tunis.
        var pickup = TunisTime.FromLocal(new DateTime(2027, 6, 28, 10, 0, 0, DateTimeKind.Unspecified));

        pickup.ShouldBe(new DateTimeOffset(2027, 6, 28, 9, 0, 0, TimeSpan.Zero));
        pickup.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void FromLocal_ignores_the_datetime_kind()
    {
        var expected = new DateTimeOffset(2027, 7, 3, 9, 0, 0, TimeSpan.Zero);

        TunisTime.FromLocal(new DateTime(2027, 7, 3, 10, 0, 0, DateTimeKind.Utc)).ShouldBe(expected);
        TunisTime.FromLocal(new DateTime(2027, 7, 3, 10, 0, 0, DateTimeKind.Local)).ShouldBe(expected);
        TunisTime.FromLocal(new DateOnly(2027, 7, 3), new TimeOnly(10, 0)).ShouldBe(expected);
    }

    [Fact]
    public void Dates_are_evaluated_in_tunis()
    {
        // 23:30 UTC on 30 June is already 1 July in Tunis.
        var instant = new DateTimeOffset(2027, 6, 30, 23, 30, 0, TimeSpan.Zero);

        TunisTime.DateOf(instant).ShouldBe(new DateOnly(2027, 7, 1));
        TunisTime.StartOfDay(new DateOnly(2027, 7, 1)).ShouldBe(new DateTimeOffset(2027, 6, 30, 23, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Now_and_today_come_from_the_injected_time_provider()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2027, 12, 31, 23, 15, 0, TimeSpan.Zero));

        TunisTime.Now(clock).ShouldBe(new DateTimeOffset(2028, 1, 1, 0, 15, 0, TimeSpan.FromHours(1)));
        TunisTime.Today(clock).ShouldBe(new DateOnly(2028, 1, 1));
        Should.Throw<ArgumentNullException>(() => TunisTime.Now(null!));
    }
}
