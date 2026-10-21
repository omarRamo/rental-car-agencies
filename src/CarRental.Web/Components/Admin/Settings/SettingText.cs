using System.Globalization;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Resources.Admin.Settings;
using Microsoft.Extensions.Localization;

namespace CarRental.Web.Components.Admin.Settings;

/// <summary>
/// Texts of a setting on the settings screen: label, help and group title from <see cref="SettingLabels"/> (keys derived
/// from the setting key; a missing label shows the key itself, a missing help nothing), and the display of a stored
/// value in the user's language according to the kind of the setting.
/// </summary>
public static class SettingText
{
    public static string Label(IStringLocalizer<SettingLabels> labels, string key)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var text = labels[$"Setting.{key}.Label"];
        return text.ResourceNotFound ? key : text.Value;
    }

    public static string? Help(IStringLocalizer<SettingLabels> labels, string key)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var text = labels[$"Setting.{key}.Help"];
        return text.ResourceNotFound ? null : text.Value;
    }

    public static string Group(IStringLocalizer<SettingLabels> labels, string group)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var text = labels[$"Group.{group}"];
        return text.ResourceNotFound ? group : text.Value;
    }

    /// <summary>A stored value as the user reads it (the stored text itself when it does not parse).</summary>
    public static string Display(IStringLocalizer<SettingsAdminResources> localizer, SettingKind kind, string value)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(value);
        var culture = CultureInfo.CurrentCulture;
        return kind switch
        {
            SettingKind.Duration when SettingValues.TryParseDuration(value, out var duration) => Duration(localizer, duration),
            SettingKind.Number when SettingValues.TryParseNumber(value, out var number) => number.ToString("0.#####", culture),
            SettingKind.WholeNumber when SettingValues.TryParseWholeNumber(value, out var whole) => whole.ToString("N0", culture),
            SettingKind.Amount when SettingValues.TryParseNumber(value, out var amount) => Money.Tnd(amount).ToString("N", culture),
            SettingKind.Boolean when SettingValues.TryParseBoolean(value, out var flag) => flag ? localizer["Settings.Value.Yes"] : localizer["Settings.Value.No"],
            SettingKind.TimeRange when SettingValues.TryParseTimeRange(value, out var range) =>
                localizer["Settings.Value.TimeRange", range.Start.ToString("t", culture), range.End.ToString("t", culture)],
            SettingKind.Text when value.Length == 0 => localizer["Settings.Value.Empty"],
            _ => value,
        };
    }

    /// <summary>A duration in days, hours and minutes, e.g. « 1 j 12 h » or « 59 min ».</summary>
    public static string Duration(IStringLocalizer<SettingsAdminResources> localizer, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var parts = new List<string>(3);
        if (duration.Days > 0)
        {
            parts.Add(localizer["Settings.Value.Days", duration.Days]);
        }

        if (duration.Hours > 0)
        {
            parts.Add(localizer["Settings.Value.Hours", duration.Hours]);
        }

        if (duration.Minutes > 0 || parts.Count == 0)
        {
            parts.Add(localizer["Settings.Value.Minutes", duration.Minutes]);
        }

        return string.Join(' ', parts);
    }
}
