using System.Text.RegularExpressions;
using CarRental.Domain.Common;

namespace CarRental.Domain.Referential;

/// <summary>
/// A business parameter (hold duration, VAT rate, preparation buffer…) stored as an invariant string.
/// <see cref="AgencyId"/> is <see langword="null"/> for the platform-wide value; an agency may override it later.
/// Typed reading and validation are done by setting definitions in the application layer.
/// </summary>
public sealed partial class Setting : AggregateRoot
{
    public const int KeyMaxLength = 100;
    public const int ValueMaxLength = 1000;

    private Setting()
    {
    }

    private Setting(Guid id, string key, string value, Guid? agencyId)
        : base(id)
    {
        Key = key;
        Value = value;
        AgencyId = agencyId;
    }

    /// <summary>Dotted lower-case key, e.g. <c>bookings.hold_duration</c>.</summary>
    public string Key { get; private set; } = null!;

    /// <summary>Value in invariant format (TimeSpan "c", decimal with a dot…).</summary>
    public string Value { get; private set; } = null!;

    public Guid? AgencyId { get; private set; }

    public static Result<Setting> Create(string key, string value, Guid? agencyId = null)
    {
        var normalizedKey = key?.Trim() ?? string.Empty;
        if (normalizedKey.Length > KeyMaxLength || !KeyPattern().IsMatch(normalizedKey))
        {
            return ReferentialErrors.SettingKeyInvalid;
        }

        if (value is null || value.Length > ValueMaxLength)
        {
            return ReferentialErrors.SettingValueInvalid;
        }

        if (agencyId == Guid.Empty)
        {
            return ReferentialErrors.AgencyRequired;
        }

        var setting = new Setting(NewId(), normalizedKey, value, agencyId);
        setting.Raise(new SettingChanged(setting.Id, normalizedKey, agencyId, PreviousValue: null, NewValue: value));
        return setting;
    }

    public Result ChangeValue(string value)
    {
        if (value is null || value.Length > ValueMaxLength)
        {
            return ReferentialErrors.SettingValueInvalid;
        }

        if (string.Equals(value, Value, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        var previous = Value;
        Value = value;
        Raise(new SettingChanged(Id, Key, AgencyId, previous, value));
        return Result.Success();
    }

    /// <summary>
    /// Takes back the value of an agency (the platform value applies again): raises <see cref="SettingRemoved"/>, then the
    /// caller deletes the row. The platform value always exists: it goes back to its default instead.
    /// </summary>
    public Result Remove()
    {
        if (AgencyId is null)
        {
            return ReferentialErrors.PlatformSettingRequired;
        }

        Raise(new SettingRemoved(Id, Key, AgencyId, Value));
        return Result.Success();
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*([.][a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
