using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Application.Pricing.Contracts;

/// <summary>
/// Error codes returned by <see cref="IPricingService"/> that callers may handle specifically (texts: resources
/// <c>Error.{code}</c>). Codes are stable.
/// </summary>
public static class PricingErrorCodes
{
    /// <summary>The stay is shorter than the minimum duration of a season or event it touches (see <see cref="TryGetRequiredMinimumDays"/>).</summary>
    public const string MinimumDurationNotMet = "pricing.minimum_duration_not_met";

    /// <summary>The agency has no published rate plan.</summary>
    public const string NoPublishedPlan = "pricing.no_published_plan";

    /// <summary>The channel is priced with its own grid, which is not published.</summary>
    public const string ChannelPlanMissing = "pricing.channel_plan_missing";

    /// <summary>The category is unknown, inactive or not priced on the requested dates.</summary>
    public const string CategoryNotFound = "pricing.category_not_found";

    public const string CategoryNotPriced = "pricing.category_not_priced";

    /// <summary>The return is not after the pick-up.</summary>
    public const string PeriodInvalid = "pricing.period_invalid";

    /// <summary>An extra code is unknown or inactive.</summary>
    public const string ExtraUnknown = "pricing.extra_unknown";

    /// <summary>
    /// Reads the number of days required by a <see cref="MinimumDurationNotMet"/> error (the largest minimum duration of
    /// the seasons and events touched by the rental days).
    /// </summary>
    public static bool TryGetRequiredMinimumDays(Error error, out int requiredDays)
    {
        ArgumentNullException.ThrowIfNull(error);
        requiredDays = 0;
        const string marker = "(minimum: ";
        var start = error.Code == MinimumDurationNotMet ? error.Message.IndexOf(marker, StringComparison.Ordinal) : -1;
        if (start < 0)
        {
            return false;
        }

        start += marker.Length;
        var end = error.Message.IndexOf(' ', start);
        return end > start
            && int.TryParse(error.Message.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out requiredDays);
    }
}
