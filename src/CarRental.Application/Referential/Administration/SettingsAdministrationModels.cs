using CarRental.Application.Referential.Contracts;

namespace CarRental.Application.Referential.Administration;

/// <summary>Everything the settings screen shows: the agencies and every declared setting with its stored values.</summary>
public sealed record SettingsOverview(IReadOnlyList<SettingsAgency> Agencies, IReadOnlyList<SettingItem> Settings);

/// <summary>An agency that can override settings.</summary>
public sealed record SettingsAgency(Guid Id, string Name, bool IsActive);

/// <summary>A declared setting, its default, its platform value and the values of the agencies that override it.</summary>
public sealed record SettingItem(
    string Key,
    string Group,
    SettingEditor Editor,
    SettingScope Scope,
    string DefaultValue,
    string? PlatformValue,
    IReadOnlyDictionary<Guid, string> AgencyValues)
{
    /// <summary>The platform value, or the default while none is stored.</summary>
    public string PlatformOrDefault => PlatformValue ?? DefaultValue;

    /// <summary>Whether an agency may have its own value.</summary>
    public bool AgencyOverridable => Scope == SettingScope.Agency;

    /// <summary>
    /// The value read for <paramref name="agencyId"/> (same precedence as <see cref="ISettingsReader"/>): its own value,
    /// otherwise the platform value, otherwise the default.
    /// </summary>
    public string EffectiveFor(Guid? agencyId) =>
        AgencyOverridable && agencyId is { } id && AgencyValues.TryGetValue(id, out var value) ? value : PlatformOrDefault;
}
