using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Reporting;

namespace CarRental.Application.Reporting.Indicators;

/// <summary>A category of the pace table, in display order.</summary>
internal sealed record PaceCategory(Guid Id, string Code);

/// <summary>
/// The booking pace (SPEC §8 "Carnet à 30, 60 et 90 jours : jours déjà vendus par catégorie", ADR-0046), from the files
/// and the fleet, never from the snapshots: for each horizon H, the rented days of the next H days (from today included)
/// sold to firm files, those of tentative files (requests, options, awaiting payment), and the rentable vehicle-days of the
/// fleet over the same days; a day goes to the category sold. One year earlier, the same count for the same lead time:
/// the files as they stood at the same instant one year before (status from their timeline).
/// </summary>
internal static class PaceCalculator
{
    public static readonly IReadOnlyList<int> DefaultHorizons = [30, 60, 90];

    public static PaceReport Compute(
        DateOnly asOf,
        IReadOnlyList<int> horizons,
        IReadOnlyList<PaceCategory> categories,
        IReadOnlyList<BookingFactsInfo> bookings,
        IReadOnlyList<FleetVehicleHistoryInfo> fleet,
        IReadOnlyList<BookingFactsInfo> lastYearBookings,
        DateOnly lastYearAsOf,
        DateTimeOffset lastYearInstant)
    {
        ArgumentNullException.ThrowIfNull(horizons);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(bookings);
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(lastYearBookings);

        var sold = DaysByCategory(bookings.Where(b => RevenueRecognition.Counts(b.Status)));
        var tentative = DaysByCategory(bookings.Where(b => RevenueRecognition.IsTentative(b.Status)));
        var capacity = fleet
            .SelectMany(v => v.Days)
            .Where(d => d.IsRentable)
            .Select(d => (Category: d.CategoryId, d.Day))
            .ToList();

        // Without any file created by then, last year says nothing (the platform did not exist yet): no comparison.
        var hasLastYear = lastYearBookings.Any(b => b.CreatedAtUtc <= lastYearInstant);
        var lastYear = DaysByCategory(lastYearBookings.Where(b => RevenueRecognition.StatusAt(b, lastYearInstant) is { } status && RevenueRecognition.Counts(status)));

        PaceHorizon HorizonOf(int days, Func<Guid, bool> inCategory)
        {
            var end = asOf.AddDays(days - 1);
            var lastYearEnd = lastYearAsOf.AddDays(days - 1);
            return new PaceHorizon(
                days,
                sold.Count(d => inCategory(d.Category) && d.Day >= asOf && d.Day <= end),
                tentative.Count(d => inCategory(d.Category) && d.Day >= asOf && d.Day <= end),
                capacity.Count(d => inCategory(d.Category) && d.Day >= asOf && d.Day <= end),
                hasLastYear ? lastYear.Count(d => inCategory(d.Category) && d.Day >= lastYearAsOf && d.Day <= lastYearEnd) : null);
        }

        var rows = categories
            .Select(category => new CategoryPace(category.Id, category.Code, [.. horizons.Select(h => HorizonOf(h, id => id == category.Id))]))
            .ToList();
        return new PaceReport(asOf, horizons, rows, [.. horizons.Select(h => HorizonOf(h, _ => true))]);
    }

    /// <summary>The rented days of the files (one per day of their current quote), with the category sold.</summary>
    private static List<(Guid Category, DateOnly Day)> DaysByCategory(IEnumerable<BookingFactsInfo> bookings) =>
        [
            .. bookings
                .Where(b => b.Quote.Days > 0)
                .SelectMany(b => RentalDays.DatesOf(b.PickupAt, b.Quote.Days).Select(day => (b.CategoryId, day))),
        ];
}
