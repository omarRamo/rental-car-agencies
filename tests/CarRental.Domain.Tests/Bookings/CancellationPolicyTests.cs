using CarRental.Domain.Bookings;
using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Bookings;

/// <summary>Free until D-x (Tunis calendar days), then a percentage of the total TTC — for firm bookings only.</summary>
public sealed class CancellationPolicyTests
{
    private static readonly CancellationPolicy Policy = new(freeUntilDaysBefore: 3, feePercent: 0.20m);

    private static readonly DateTimeOffset Pickup = TunisTime.FromLocal(new DateTime(2027, 7, 10, 10, 0, 0, DateTimeKind.Unspecified));

    private static DateTimeOffset Tunis(int day, int hour, int minute = 0) =>
        TunisTime.FromLocal(new DateTime(2027, 7, day, hour, minute, 0, DateTimeKind.Unspecified));

    [Theory]
    [InlineData(1, 9, 0, 0)]
    [InlineData(7, 0, 0, 0)]
    [InlineData(7, 23, 59, 0)]
    [InlineData(8, 0, 0, 136.280)]
    [InlineData(10, 9, 0, 136.280)]
    public void A_confirmed_booking_is_free_until_D_minus_x_included(int day, int hour, int minute, double expectedFee)
    {
        Policy.FeeFor(BookingStatus.Confirmed, Money.Tnd(681.400m), Pickup, Tunis(day, hour, minute))
            .ShouldBe(Money.Tnd((decimal)expectedFee));
    }

    [Theory]
    [InlineData(BookingStatus.PendingValidation)]
    [InlineData(BookingStatus.InformationRequested)]
    [InlineData(BookingStatus.OptionHeld)]
    [InlineData(BookingStatus.AwaitingPayment)]
    public void Requests_options_and_unpaid_validations_are_cancelled_for_free(BookingStatus status)
    {
        Policy.FeeFor(status, Money.Tnd(681.400m), Pickup, Tunis(10, 9)).ShouldBe(Money.Zero);
    }

    [Fact]
    public void The_fee_is_rounded_to_the_millime_away_from_zero()
    {
        new CancellationPolicy(1, 0.15m).FeeFor(BookingStatus.Confirmed, Money.Tnd(100.005m), Pickup, Tunis(10, 9))
            .ShouldBe(Money.Tnd(15.001m));
        Policy.FreeUntil(Pickup).ShouldBe(new DateOnly(2027, 7, 7));
    }

    [Fact]
    public void The_policy_is_bounded()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new CancellationPolicy(-1, 0.2m));
        Should.Throw<ArgumentOutOfRangeException>(() => new CancellationPolicy(3, 1.5m));
        new CancellationPolicy(3, 0m).FeeFor(BookingStatus.Confirmed, Money.Tnd(500m), Pickup, Tunis(10, 9)).ShouldBe(Money.Zero);
    }
}
