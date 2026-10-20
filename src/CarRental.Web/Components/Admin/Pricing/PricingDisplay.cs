using System.Globalization;
using System.Text.RegularExpressions;
using CarRental.Domain.Common;
using Microsoft.Extensions.Localization;

namespace CarRental.Web.Components.Admin.Pricing;

/// <summary>Formatting shared by the pricing screens: amounts, percentages, dates and the localized quote line labels.</summary>
internal static partial class PricingDisplay
{
    /// <summary>Amount with its millimes in the current culture, e.g. <c>681,400 TND</c> in French.</summary>
    public static string Amount(Money amount) => amount.ToString("N", CultureInfo.CurrentCulture);

    public static string Percent(decimal percent) =>
        string.Create(CultureInfo.CurrentCulture, $"{percent:0.##} %");

    public static string Date(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);

    /// <summary>An instant shown in Tunis time.</summary>
    public static string Instant(DateTimeOffset instant) => TunisTime.ToTunis(instant).ToString("g", CultureInfo.CurrentCulture);

    public static string ChannelKey(SalesChannel? channel) => channel is { } value ? $"Channel.{value}" : "Channel.Default";

    /// <summary>
    /// The label of a quote line: the resource <paramref name="key"/> formatted with its invariant arguments, numbers being
    /// shown in the current culture (amounts with three decimals, percentages with up to two).
    /// </summary>
    public static string QuoteLabel(IStringLocalizer localizer, string key, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(arguments);
        var text = localizer[key, [.. arguments.Select(Localize)]];
        return text.ResourceNotFound ? key : text.Value;
    }

    private static object Localize(string argument)
    {
        if (AmountPattern().IsMatch(argument) && decimal.TryParse(argument, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            return amount.ToString("N3", CultureInfo.CurrentCulture);
        }

        return NumberPattern().IsMatch(argument) && decimal.TryParse(argument, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("0.##", CultureInfo.CurrentCulture)
            : argument;
    }

    [GeneratedRegex("^-?[0-9]+[.][0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex AmountPattern();

    [GeneratedRegex("^-?[0-9]+([.][0-9]{1,2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
