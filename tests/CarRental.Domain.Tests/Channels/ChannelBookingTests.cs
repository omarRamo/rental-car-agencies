using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using static CarRental.Domain.Tests.Channels.ChannelTestData;

namespace CarRental.Domain.Tests.Channels;

/// <summary>Commission rule (reported amount wins, rate otherwise, net consistent) and the life of a channel booking.</summary>
public sealed class ChannelBookingTests
{
    private static readonly string Fingerprint = new('a', ChannelBooking.FingerprintLength);

    [Fact]
    public void The_rate_of_the_channel_applies_to_the_gross_price_rounded_to_the_millime()
    {
        var amounts = ChannelCommissionRule.Compute(Tnd(480.005m), null, null, 0.15m).Value;

        amounts.Commission.ShouldBe(Tnd(72.001m), "72.00075 rounded away from zero");
        amounts.Net.ShouldBe(Tnd(408.004m));
        amounts.Source.ShouldBe(ChannelCommissionSource.Rule);
        amounts.Rate.ShouldBe(0.15m);
    }

    [Fact]
    public void A_commission_reported_by_the_channel_wins_over_the_rate()
    {
        var amounts = ChannelCommissionRule.Compute(Tnd(500m), Tnd(60m), Tnd(440m), 0.15m).Value;

        amounts.Commission.ShouldBe(Tnd(60m));
        amounts.Net.ShouldBe(Tnd(440m));
        amounts.Source.ShouldBe(ChannelCommissionSource.Reported);
        amounts.Rate.ShouldBe(0m);
    }

    [Fact]
    public void Inconsistent_or_foreign_amounts_are_refused()
    {
        ChannelCommissionRule.Compute(Tnd(0m), null, null, 0.15m).Error.ShouldBe(ChannelsErrors.AmountInvalid);
        ChannelCommissionRule.Compute(Money.Of(100m, Currency.Eur), null, null, 0.15m).Error.ShouldBe(ChannelsErrors.AmountInvalid);
        ChannelCommissionRule.Compute(Tnd(100m), Tnd(101m), null, 0.15m).Error.ShouldBe(ChannelsErrors.AmountInvalid);
        ChannelCommissionRule.Compute(Tnd(100m), Tnd(-1m), null, 0.15m).Error.ShouldBe(ChannelsErrors.AmountInvalid);
        ChannelCommissionRule.Compute(Tnd(100m), Tnd(10m), Tnd(91m), 0.15m).Error.ShouldBe(ChannelsErrors.AmountsInconsistent);
        ChannelCommissionRule.Compute(Tnd(100m), null, Tnd(80m), 0.15m).Error.ShouldBe(ChannelsErrors.AmountsInconsistent);
        ChannelCommissionRule.Compute(Tnd(100m), null, null, 1.5m).Error.ShouldBe(ChannelsErrors.CommissionRateInvalid);
        ChannelCommissionRule.Compute(Tnd(2_000_000m), null, null, 0.1m).Error.ShouldBe(ChannelsErrors.AmountInvalid);
    }

    [Property(MaxTest = 500)]
    public Property Gross_is_always_commission_plus_net_and_the_commission_never_exceeds_the_gross()
    {
        var cases =
            from millimes in Gen.Choose(1, 5_000_000)
            from basisPoints in Gen.Choose(0, 10_000)
            select (Gross: Tnd(millimes / 1000m), Rate: basisPoints / 10_000m);

        return Prop.ForAll(cases.ToArbitrary(), c =>
        {
            var amounts = ChannelCommissionRule.Compute(c.Gross, null, null, c.Rate).Value;
            return amounts.Commission + amounts.Net == c.Gross && !amounts.Commission.IsNegative && amounts.Commission <= c.Gross;
        });
    }

    [Fact]
    public void A_recorded_booking_keeps_its_reference_amounts_and_engine_total()
    {
        var booking = Record(out var details);

        booking.ChannelReference.ShouldBe("BDC-4711");
        booking.State.ShouldBe(ChannelBookingState.Active);
        booking.Gross.ShouldBe(Tnd(450m));
        booking.Commission.ShouldBe(Tnd(67.5m));
        booking.Net.ShouldBe(Tnd(382.5m));
        booking.EngineTotalInclVat.ShouldBe(details.EngineTotalInclVat);
        booking.ReceivedAtUtc.ShouldBe(Now);
        booking.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<ChannelBookingRecorded>();
    }

    [Fact]
    public void An_invalid_reference_is_refused()
    {
        var details = Details() with { ChannelReference = "BDC 4711" };

        ChannelBooking.Record(details, Amounts(450m), Now).Error.ShouldBe(ChannelsErrors.ReferenceInvalid);
    }

    [Fact]
    public void A_modification_replaces_the_amounts_and_dates_and_a_cancellation_drops_the_commission()
    {
        var booking = Record(out _);
        var category = Guid.CreateVersion7();

        booking.Amend(Amounts(600m), category, Now.AddDays(10), Now.AddDays(15), Tnd(598m), ChannelPaymentMode.PrepaidToChannel, Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        booking.ModificationCount.ShouldBe(1);
        booking.CategoryId.ShouldBe(category);
        booking.PaymentMode.ShouldBe(ChannelPaymentMode.PrepaidToChannel);
        booking.Gross.ShouldBe(Tnd(600m));

        booking.Cancel(null, Now.AddHours(2)).IsSuccess.ShouldBeTrue();
        booking.Cancel(null, Now.AddHours(3)).IsSuccess.ShouldBeTrue("idempotent");
        booking.State.ShouldBe(ChannelBookingState.Cancelled);
        booking.Commission.ShouldBe(Money.Zero);
        booking.Net.ShouldBe(Tnd(600m));
        booking.CancelledAtUtc.ShouldBe(Now.AddHours(2));
        booking.Amend(Amounts(600m), category, Now.AddDays(10), Now.AddDays(15), Tnd(598m), ChannelPaymentMode.PrepaidToChannel, Now).Error.ShouldBe(ChannelsErrors.BookingCancelled);
        booking.DomainEvents.OfType<ChannelBookingCancelled>().ShouldHaveSingleItem();
    }

    [Fact]
    public void A_commission_reported_with_the_cancellation_is_kept()
    {
        var booking = Record(out _);

        booking.Cancel(Tnd(1000m), Now).Error.ShouldBe(ChannelsErrors.AmountInvalid);
        booking.Cancel(Tnd(20m), Now).IsSuccess.ShouldBeTrue();

        booking.Commission.ShouldBe(Tnd(20m));
        booking.CommissionSource.ShouldBe(ChannelCommissionSource.Reported);
    }

    [Fact]
    public void The_state_follows_the_rental_file_but_a_cancellation_is_final()
    {
        var booking = Record(out _);

        booking.Mirror(ChannelBookingState.NoShow, Now);
        booking.State.ShouldBe(ChannelBookingState.NoShow);
        booking.Mirror(ChannelBookingState.Cancelled, Now);
        booking.State.ShouldBe(ChannelBookingState.Cancelled);
        booking.Commission.ShouldBe(Money.Zero);
        booking.Mirror(ChannelBookingState.Active, Now);
        booking.State.ShouldBe(ChannelBookingState.Cancelled);
    }

    private static ChannelAmounts Amounts(decimal gross) => ChannelCommissionRule.Compute(Tnd(gross), null, null, 0.15m).Value;

    private static ChannelBookingDetails Details() => new(
        Guid.CreateVersion7(),
        AgencyId,
        SalesChannel.BookingCom,
        Guid.CreateVersion7(),
        "LOC-2027-000123",
        " BDC-4711 ",
        ChannelBookingSource.Inbound,
        ChannelPaymentMode.PayOnSite,
        Guid.CreateVersion7(),
        Now.AddDays(3),
        Now.AddDays(6),
        Tnd(447m),
        Fingerprint);

    private static ChannelBooking Record(out ChannelBookingDetails details)
    {
        details = Details();
        return ChannelBooking.Record(details, Amounts(450m), Now).Value;
    }
}
