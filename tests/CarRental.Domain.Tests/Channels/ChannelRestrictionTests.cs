using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Channels.ChannelTestData;

namespace CarRental.Domain.Tests.Channels;

/// <summary>
/// Quotas and stop-sells: validation, the engine view over a range (clipped, merged, location stop-sells apart) and the
/// capacity a channel sees once the availability engine applies them.
/// </summary>
public sealed class ChannelRestrictionTests
{
    private static readonly Guid CategoryId = Guid.CreateVersion7();
    private static readonly Guid TunisAirport = Guid.CreateVersion7();

    private static DateOnly July(int day) => new(2027, 7, day);

    private static Period Days(DateOnly from, DateOnly toExcluded) => Period.Create(TunisTime.StartOfDay(from), TunisTime.StartOfDay(toExcluded)).Value;

    [Fact]
    public void A_reservation_keeps_vehicles_for_the_whole_category_on_inclusive_tunis_dates()
    {
        var quota = ChannelQuota.Reserve(NewChannel(), CategoryId, July(1), July(31), 2, "  Haute saison ", Stamp).Value;

        quota.Kind.ShouldBe(ChannelQuotaKind.Reserve);
        quota.ReservedVehicles.ShouldBe(2);
        quota.LocationId.ShouldBeNull();
        quota.Note.ShouldBe("Haute saison");
        quota.SalesChannel.ShouldBe(SalesChannel.BookingCom);
        quota.Period.ShouldBe(Days(July(1), new DateOnly(2027, 8, 1)));
        quota.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<ChannelQuotaAdded>();
    }

    [Fact]
    public void Restrictions_are_validated()
    {
        var channel = NewChannel();

        ChannelQuota.Reserve(channel, Guid.Empty, July(1), July(2), 1, null, Stamp).Error.ShouldBe(ChannelsErrors.RestrictionCategoryRequired);
        ChannelQuota.Reserve(channel, CategoryId, July(2), July(1), 1, null, Stamp).Error.ShouldBe(ChannelsErrors.RestrictionPeriodInvalid);
        ChannelQuota.Reserve(channel, CategoryId, July(1), July(1).AddDays(ChannelQuota.MaxDays), 1, null, Stamp).Error.ShouldBe(ChannelsErrors.RestrictionPeriodInvalid);
        ChannelQuota.Reserve(channel, CategoryId, Today.AddDays(-5), Today.AddDays(-1), 1, null, Stamp).Error.ShouldBe(ChannelsErrors.RestrictionEnded);
        ChannelQuota.Reserve(channel, CategoryId, July(1), July(2), 0, null, Stamp).Error.ShouldBe(ChannelsErrors.RestrictionVehiclesInvalid);
        ChannelQuota.Reserve(channel, CategoryId, July(1), July(2), 1, new string('n', ChannelQuota.NoteMaxLength + 1), Stamp).Error.ShouldBe(ChannelsErrors.RestrictionNoteTooLong);
        ChannelQuota.StopSell(channel, CategoryId, Guid.Empty, July(1), July(2), null, Stamp).Error.ShouldBe(ChannelsErrors.RestrictionLocationNotAllowed);
        ChannelQuota.Reserve(channel, CategoryId, Today.AddDays(-5), Today, 1, null, Stamp).IsSuccess.ShouldBeTrue("a restriction ending today still applies");

        var reserve = ChannelQuota.Reserve(channel, CategoryId, July(1), July(2), 1, null, Stamp).Value;
        reserve.Change(TunisAirport, July(1), July(2), 1, null, Stamp).Error.ShouldBe(ChannelsErrors.RestrictionLocationNotAllowed);
    }

    [Fact]
    public void A_stop_sell_can_be_limited_to_a_pick_up_location_and_changed()
    {
        var stop = ChannelQuota.StopSell(NewChannel(), CategoryId, TunisAirport, July(10), July(12), null, Stamp).Value;

        stop.Change(null, July(11), July(14), 5, "Plus de comptoir", Stamp).IsSuccess.ShouldBeTrue();

        stop.LocationId.ShouldBeNull();
        stop.ReservedVehicles.ShouldBe(0, "a stop-sell keeps no vehicle count");
        stop.StartDate.ShouldBe(July(11));
        stop.EndDate.ShouldBe(July(14));
        stop.Note.ShouldBe("Plus de comptoir");
        stop.DomainEvents.OfType<ChannelQuotaChanged>().ShouldHaveSingleItem();
        stop.MarkRemoved(Stamp);
        stop.DomainEvents[^1].ShouldBeOfType<ChannelQuotaRemoved>();
    }

    [Fact]
    public void The_engine_view_clips_reservations_merges_stop_sells_and_leaves_located_stop_sells_out()
    {
        ChannelRestrictionRule[] rules =
        [
            new(ChannelQuotaKind.Reserve, null, July(1), July(31), 2),
            new(ChannelQuotaKind.Reserve, null, July(15), new DateOnly(2027, 8, 15), 1),
            new(ChannelQuotaKind.StopSell, null, July(10), July(12), 0),
            new(ChannelQuotaKind.StopSell, null, July(13), July(14), 0),
            new(ChannelQuotaKind.StopSell, TunisAirport, July(20), July(21), 0),
            new(ChannelQuotaKind.Reserve, null, new DateOnly(2027, 9, 1), new DateOnly(2027, 9, 30), 4),
        ];
        var range = Days(July(5), July(20));

        var view = ChannelRestrictionRules.ForEngine(rules, range);

        view.Reserved.ShouldBe([new ReservedPeriod(range, 2), new ReservedPeriod(Days(July(15), July(20)), 1)]);
        view.StopSales.ShouldBe([Days(July(10), July(15))], "touching stop-sells are merged");
        ChannelRestrictionRules.ForEngine(rules, Days(new DateOnly(2027, 12, 1), new DateOnly(2027, 12, 2))).ShouldBe(ChannelRestrictionView.None);
    }

    [Fact]
    public void A_located_stop_sell_stops_the_rentals_that_touch_it_at_its_location_only()
    {
        ChannelRestrictionRule[] rules = [new(ChannelQuotaKind.StopSell, TunisAirport, July(20), July(21), 0)];

        ChannelRestrictionRules.StopsSellingAt(rules, TunisAirport, Days(July(18), July(20))).ShouldBeFalse("the rental ends when the stop starts");
        ChannelRestrictionRules.StopsSellingAt(rules, TunisAirport, Days(July(19), July(21))).ShouldBeTrue();
        ChannelRestrictionRules.StopsSellingAt(rules, Guid.CreateVersion7(), Days(July(19), July(21))).ShouldBeFalse();
        ChannelRestrictionRules.StopSalesAt(rules, TunisAirport, Days(July(1), July(31))).ShouldBe([Days(July(20), July(22))]);
    }

    [Fact]
    public void Quota_arithmetic_the_channel_sees_the_fleet_minus_the_reservations_and_nothing_during_a_stop_sell()
    {
        // Five vehicles; keep 2 for the other channels in July, plus 1 more from the 15th; stop the sales on the 10th–12th.
        var always = Days(new DateOnly(2027, 6, 1), new DateOnly(2027, 9, 1));
        var vehicles = Enumerable.Range(1, 5).Select(i => new RentableVehicle(Guid.CreateVersion7(), $"{i} TU 100", [always])).ToList();
        ChannelRestrictionRule[] rules =
        [
            new(ChannelQuotaKind.Reserve, null, July(1), July(31), 2),
            new(ChannelQuotaKind.Reserve, null, July(15), July(31), 1),
            new(ChannelQuotaKind.StopSell, null, July(10), July(12), 0),
        ];
        var view = ChannelRestrictionRules.ForEngine(rules, always);
        var restrictions = new ChannelRestrictions(view.Reserved.Select(r => new ReservedCapacity(r.Period, r.Vehicles)), view.StopSales);
        var channel = new AvailabilityCalculator(vehicles, [], restrictions);
        var website = new AvailabilityCalculator(vehicles, [], ChannelRestrictions.None);

        static int CapacityOn(AvailabilityCalculator calculator, DateOnly day) =>
            calculator.Capacity.ValueAt(TunisTime.FromLocal(day, new TimeOnly(12, 0)));

        CapacityOn(channel, new DateOnly(2027, 6, 30)).ShouldBe(5);
        CapacityOn(channel, July(1)).ShouldBe(3);
        CapacityOn(channel, July(11)).ShouldBe(0);
        CapacityOn(channel, July(13)).ShouldBe(3);
        CapacityOn(channel, July(20)).ShouldBe(2);
        CapacityOn(channel, new DateOnly(2027, 8, 1)).ShouldBe(5);
        CapacityOn(website, July(11)).ShouldBe(5, "a stop-sell of the channel never blocks another channel");
        CapacityOn(website, July(20)).ShouldBe(5);
    }

    [Fact]
    public void Reserving_more_vehicles_than_the_fleet_leaves_the_channel_nothing_never_less()
    {
        var always = Days(new DateOnly(2027, 6, 1), new DateOnly(2027, 9, 1));
        var vehicles = new[] { new RentableVehicle(Guid.CreateVersion7(), "1 TU 1", [always]) };
        var view = ChannelRestrictionRules.ForEngine([new(ChannelQuotaKind.Reserve, null, July(1), July(31), 3)], always);
        var channel = new AvailabilityCalculator(vehicles, [], new ChannelRestrictions(view.Reserved.Select(r => new ReservedCapacity(r.Period, r.Vehicles)), view.StopSales));

        channel.Capacity.ValueAt(TunisTime.FromLocal(July(5), new TimeOnly(9, 0))).ShouldBe(0);
        channel.Check(Days(July(5), July(6)), TimeSpan.FromHours(2)).IsAvailable.ShouldBeFalse();
    }
}
