using System.Globalization;
using System.Resources;
using CarRental.Application.Bookings.Handover;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using CarRental.Web.Resources.Admin.Handovers;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace CarRental.Web.Components.Admin.Handovers;

/// <summary>
/// Texts and formats of the hand-over screens, the contract and its e-mail: labels of the return charges
/// (<c>ReturnCharge.Label.{Kind}</c> and their detail <c>ReturnCharge.Detail.{Kind}</c>), damages, views and fuel.
/// </summary>
internal static class HandoverText
{
    /// <summary>The resources of the hand-overs in a given culture (contract PDF and e-mail, built outside a request).</summary>
    public static ResourceManager Resources { get; } = new(typeof(HandoversResources).FullName!, typeof(HandoversResources).Assembly);

    public static DialogOptions Dialog(MaxWidth maxWidth = MaxWidth.Small) =>
        new() { MaxWidth = maxWidth, FullWidth = true, CloseOnEscapeKey = true };

    public static CultureInfo CultureOf(ContractLanguage language) =>
        CultureInfo.GetCultureInfo(language == ContractLanguage.English ? "en" : "fr");

    /// <summary>The label of a return charge: its kind, the detail of its arguments, then the agent's description.</summary>
    public static string Charge(Func<string, string> text, ReturnChargeKind kind, IReadOnlyList<string> arguments, string? description, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(arguments);
        var label = text($"ReturnCharge.Label.{kind}");
        if (arguments.Count > 0 && text($"ReturnCharge.Detail.{kind}") is { } detail && detail != $"ReturnCharge.Detail.{kind}")
        {
            label = $"{label} ({string.Format(culture, detail, [.. arguments.Select(a => Argument(a, culture))])})";
        }

        return string.IsNullOrWhiteSpace(description) ? label : $"{label} — {description}";
    }

    /// <summary>The label of a charge on a screen (current UI culture).</summary>
    public static string Charge(IStringLocalizer localizer, ReturnChargeView charge)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(charge);
        return Charge(key => localizer[key], charge.Kind, charge.LabelArguments, charge.Description, CultureInfo.CurrentCulture);
    }

    /// <summary>A text of the hand-over resources in <paramref name="culture"/>, formatted with <paramref name="arguments"/>.</summary>
    public static string Format(CultureInfo culture, string name, params object[] arguments)
    {
        var template = Resources.GetString(name, culture) ?? name;
        return arguments.Length == 0 ? template : string.Format(culture, template, arguments);
    }

    public static string Fuel(int? eighths) => eighths is { } value ? string.Create(CultureInfo.InvariantCulture, $"{value}/8") : "—";

    public static string Kilometres(int? value, CultureInfo culture) => value is { } km ? $"{km.ToString("N0", culture)} km" : "—";

    public static string Amount(Money amount, CultureInfo culture) => amount.ToString("N", culture);

    public static string Instant(DateTimeOffset instant, CultureInfo culture) => TunisTime.ToTunis(instant).ToString("g", culture);

    public static string Date(DateOnly date, CultureInfo culture) => date.ToString("d", culture);

    /// <summary>An invariant label argument shown in a culture: amounts with three decimals, other numbers as they are.</summary>
    private static string Argument(string argument, CultureInfo culture) =>
        decimal.TryParse(argument, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? argument.Contains('.', StringComparison.Ordinal) ? number.ToString("N3", culture) : number.ToString("N0", culture)
            : argument;
}
