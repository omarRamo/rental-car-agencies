using CarRental.Application.Bookings.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;

namespace CarRental.Application.Reporting.Indicators;

/// <summary>
/// Conversion of the website and of the bot (SPEC §8 "recherches → demandes → confirmées", ADR-0046), by channel and
/// source (<c>src</c> of the deep link, lower case; none for direct traffic): visits that searched and saw results (funnel
/// events, ADR-0037), requests submitted in the period (files) and those confirmed since. A WhatsApp link carries the
/// WhatsApp channel, so the bot is the WhatsApp rows.
/// </summary>
internal static class FunnelCalculator
{
    public static FunnelReport Compute(IEnumerable<FunnelCount> counts, IEnumerable<BookingFactsInfo> requests)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(requests);
        var steps = counts
            .GroupBy(c => (c.Channel, Source: Normalize(c.Source)))
            .ToDictionary(g => g.Key, g => (Searches: Visits(g, FunnelStep.Search), Results: Visits(g, FunnelStep.Results)));
        var submitted = requests
            .Where(r => r.Channel is SalesChannel.Web or SalesChannel.WhatsApp)
            .GroupBy(r => (r.Channel, Source: Normalize(r.Source)))
            .ToDictionary(g => g.Key, g => (Requests: g.Count(), Confirmed: g.Count(RevenueRecognition.WasConfirmed)));

        var rows = steps.Keys
            .Union(submitted.Keys)
            .Select(key =>
            {
                var (searches, results) = steps.GetValueOrDefault(key);
                var (requested, confirmed) = submitted.GetValueOrDefault(key);
                return new FunnelRow(key.Channel, key.Source, searches, results, requested, confirmed);
            })
            .OrderBy(r => r.Channel)
            .ThenByDescending(r => r.Searches)
            .ThenByDescending(r => r.Requests)
            .ThenBy(r => r.Source, StringComparer.Ordinal)
            .ToList();
        var total = new FunnelRow(null, null, rows.Sum(r => r.Searches), rows.Sum(r => r.Results), rows.Sum(r => r.Requests), rows.Sum(r => r.Confirmed));
        return new FunnelReport(rows, total);
    }

    /// <summary>A source as the funnel events store it: trimmed, lower case, none when blank.</summary>
    public static string? Normalize(string? source) =>
        string.IsNullOrWhiteSpace(source) ? null : source.Trim().ToLowerInvariant();

    private static int Visits(IEnumerable<FunnelCount> counts, FunnelStep step) => counts.Where(c => c.Step == step).Sum(c => c.Visits);
}
