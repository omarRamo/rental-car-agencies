using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Channels.Adapters;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Channels.Core;
using CarRental.Application.Channels.Restrictions;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;

namespace CarRental.Application.Channels.Outbound;

/// <summary>
/// Builds the outbound updates from fresh reads of the engines (the Channels module computes no price and no capacity):
/// availability from the cached availability engine for the channel (quotas and stop-sells of the whole category
/// included), cut by the stop-sells of each location; prices from the pricing engine for the channel (its rule of
/// parity, markup or own grid), as daily prices.
/// </summary>
internal sealed class ChannelSyncPayloads(
    IAvailabilityQueries availability,
    IPricingService pricing,
    ChannelRestrictionReader restrictions,
    ISettingsReader settings,
    TimeProvider clock)
{
    /// <summary>Days quoted at once to read the daily prices of the engine.</summary>
    internal const int RateWindowDays = 30;

    /// <summary>
    /// Per mapped category (or only <paramref name="categoryId"/>) and mapped location, the vehicles that can still be
    /// sold each Tunis day of the horizon: the smallest headroom of the day (never negative), 0 on a stop-sell of the
    /// location; consecutive days with the same count are one item.
    /// </summary>
    public async Task<ChannelAvailabilityUpdate> AvailabilityAsync(ChannelContext channel, Guid? categoryId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var (today, end) = await HorizonAsync(channel, cancellationToken);
        var range = Period.Create(TunisTime.StartOfDay(today), TunisTime.StartOfDay(end)).Value;
        var locations = channel.Mappings.Locations.ToList();
        var items = new List<ChannelAvailabilityItem>();
        foreach (var category in channel.Mappings.Categories.Where(c => categoryId is null || c.InternalId == categoryId))
        {
            var id = category.InternalId!.Value;
            var profile = await availability.GetCapacityProfileAsync(id, range, channel.SalesChannel, cancellationToken) ?? [];
            var daily = DailyHeadroom(profile, today, end);
            var rules = await restrictions.GetAsync(channel.AgencyId, id, channel.SalesChannel, cancellationToken);
            foreach (var location in locations)
            {
                var stops = ChannelRestrictionRules.StopSalesAt(rules, location.InternalId!.Value, range);
                var values = daily.Select((count, day) => stops.Any(stop => stop.Overlaps(DayOf(today.AddDays(day)))) ? 0 : count).ToList();
                items.AddRange(Runs(today, values).Select(run => new ChannelAvailabilityItem(category.ChannelCode, channel.Mappings.AcrissOf(id), location.ChannelCode, run.From, run.To, run.Value)));
            }
        }

        return new ChannelAvailabilityUpdate(items);
    }

    /// <summary>
    /// The daily price TTC of each mapped category over the horizon, after the channel rule, read from quotes of
    /// <see cref="RateWindowDays"/> days (no minimum duration applies to them); consecutive days with the same price are
    /// one item. A window the engine cannot price is left out.
    /// </summary>
    public async Task<ChannelRatesUpdate> RatesAsync(ChannelContext channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var location = channel.Mappings.Locations.FirstOrDefault();
        var items = new List<ChannelRateItem>();
        if (location is null)
        {
            return new ChannelRatesUpdate(Currency.Tnd.Code, items);
        }

        var (today, end) = await HorizonAsync(channel, cancellationToken);
        foreach (var category in channel.Mappings.Categories)
        {
            var id = category.InternalId!.Value;
            var prices = new SortedDictionary<DateOnly, decimal>();
            for (var start = today; start < end; start = start.AddDays(RateWindowDays))
            {
                var pickupAt = TunisTime.FromLocal(start, new TimeOnly(10, 0));
                var quote = await pricing.QuoteAsync(
                    new PriceRequest(channel.AgencyId, id, location.InternalId!.Value, location.InternalId.Value, pickupAt, pickupAt.AddDays(RateWindowDays), channel.SalesChannel),
                    cancellationToken);
                if (quote.IsFailure)
                {
                    continue;
                }

                foreach (var rate in quote.Value.DailyRates.Where(r => r.Date >= today && r.Date < end))
                {
                    prices[rate.Date] = rate.Price.Amount;
                }
            }

            foreach (var run in PriceRuns(prices))
            {
                items.Add(new ChannelRateItem(category.ChannelCode, channel.Mappings.AcrissOf(id), run.From, run.To, run.Price));
            }
        }

        return new ChannelRatesUpdate(Currency.Tnd.Code, items);
    }

    /// <summary>For each day of <c>[today, end)</c>, the smallest headroom (capacity − occupancy, never below 0) over the day.</summary>
    internal static List<int> DailyHeadroom(IReadOnlyList<CapacitySegmentInfo> profile, DateOnly today, DateOnly end)
    {
        var values = new List<int>();
        for (var day = today; day < end; day = day.AddDays(1))
        {
            var period = DayOf(day);
            var overlapping = profile.Where(s => s.Period.Overlaps(period)).Select(s => Math.Max(0, s.Remaining)).ToList();
            values.Add(overlapping.Count == 0 ? 0 : overlapping.Min());
        }

        return values;
    }

    internal static IEnumerable<(DateOnly From, DateOnly To, int Value)> Runs(DateOnly first, IReadOnlyList<int> values)
    {
        var start = 0;
        for (var i = 1; i <= values.Count; i++)
        {
            if (i == values.Count || values[i] != values[start])
            {
                if (values.Count > 0)
                {
                    yield return (first.AddDays(start), first.AddDays(i - 1), values[start]);
                }

                start = i;
            }
        }
    }

    private static IEnumerable<(DateOnly From, DateOnly To, decimal Price)> PriceRuns(SortedDictionary<DateOnly, decimal> prices)
    {
        (DateOnly From, DateOnly To, decimal Price)? run = null;
        foreach (var (date, price) in prices)
        {
            if (run is { } current && current.Price == price && current.To.AddDays(1) == date)
            {
                run = (current.From, date, price);
                continue;
            }

            if (run is { } done)
            {
                yield return done;
            }

            run = (date, date, price);
        }

        if (run is { } last)
        {
            yield return last;
        }
    }

    private static Period DayOf(DateOnly day) => Period.Create(TunisTime.StartOfDay(day), TunisTime.StartOfDay(day.AddDays(1))).Value;

    private async Task<(DateOnly Today, DateOnly End)> HorizonAsync(ChannelContext channel, CancellationToken cancellationToken)
    {
        var days = await settings.GetAsync(ChannelsSettings.PushHorizonDays, channel.AgencyId, cancellationToken);
        var today = TunisTime.DateOf(clock.GetUtcNow());
        return (today, today.AddDays(days));
    }
}
