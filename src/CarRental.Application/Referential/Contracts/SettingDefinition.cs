using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Application.Referential.Contracts;

/// <summary>
/// A typed business parameter stored as an invariant string in the Referential module (<c>referential.Settings</c>).
/// Modules declare their own definitions (e.g. in <c>{Module}/Contracts</c>) and read them through <see cref="ISettingsReader"/>;
/// the default applies until a value is stored.
/// </summary>
public abstract class SettingDefinition
{
    protected SettingDefinition(string key, string defaultValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(defaultValue);
        Key = key;
        DefaultValue = defaultValue;
    }

    /// <summary>Dotted lower-case key, e.g. <c>bookings.hold_duration</c>.</summary>
    public string Key { get; }

    /// <summary>Default value, in the invariant storage format.</summary>
    public string DefaultValue { get; }

    /// <summary>Checks that <paramref name="value"/> is a valid stored value for this setting.</summary>
    public abstract Result Validate(string? value);

    public override string ToString() => Key;
}

/// <summary>A setting of type <typeparamref name="T"/>.</summary>
public sealed class SettingDefinition<T> : SettingDefinition
{
    private readonly Func<string, Result<T>> _parse;
    private readonly Func<T, string> _format;

    public SettingDefinition(string key, T defaultValue, Func<string, Result<T>> parse, Func<T, string> format)
        : base(key, (format ?? throw new ArgumentNullException(nameof(format)))(defaultValue))
    {
        ArgumentNullException.ThrowIfNull(parse);
        Default = defaultValue;
        _parse = parse;
        _format = format;
    }

    public T Default { get; }

    public Result<T> Parse(string? value) =>
        value is null ? SettingErrors.InvalidValue(Key) : _parse(value);

    public string Format(T value) => _format(value);

    public override Result Validate(string? value)
    {
        var parsed = Parse(value);
        return parsed.IsSuccess ? Result.Success() : parsed.Error;
    }
}

/// <summary>Factories for the usual setting types, with invariant storage formats.</summary>
public static class SettingDefinitions
{
    /// <summary>A duration stored with the TimeSpan constant format (<c>1.00:00:00</c> for 24 h), between zero and 365 days.</summary>
    public static SettingDefinition<TimeSpan> Duration(string key, TimeSpan defaultValue) => new(
        key,
        defaultValue,
        value => TimeSpan.TryParseExact(value, "c", CultureInfo.InvariantCulture, out var duration)
                 && duration >= TimeSpan.Zero
                 && duration <= TimeSpan.FromDays(365)
            ? duration
            : SettingErrors.InvalidValue(key),
        duration => duration.ToString("c", CultureInfo.InvariantCulture));

    /// <summary>A decimal (rate, ratio) stored with an invariant dot, within [<paramref name="minimum"/>, <paramref name="maximum"/>].</summary>
    public static SettingDefinition<decimal> Number(string key, decimal defaultValue, decimal minimum, decimal maximum) => new(
        key,
        defaultValue,
        value => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                 && number >= minimum
                 && number <= maximum
            ? number
            : SettingErrors.InvalidValue(key),
        number => number.ToString(CultureInfo.InvariantCulture));

    /// <summary>An amount in TND with at most three decimals (millimes), stored like <c>2.000</c>; negative amounts are rejected.</summary>
    public static SettingDefinition<Money> Amount(string key, Money defaultValue) => new(
        key,
        defaultValue,
        value => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                 && number >= 0m
                 && Money.FromExact(number, Currency.Tnd) is { IsSuccess: true } money
            ? money.Value
            : SettingErrors.InvalidValue(key),
        money => money.Amount.ToString("F3", CultureInfo.InvariantCulture));

    public static SettingDefinition<bool> Boolean(string key, bool defaultValue) => new(
        key,
        defaultValue,
        value => bool.TryParse(value, out var flag) ? flag : SettingErrors.InvalidValue(key),
        flag => flag ? "true" : "false");

    public static SettingDefinition<int> WholeNumber(string key, int defaultValue, int minimum, int maximum) => new(
        key,
        defaultValue,
        value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                 && number >= minimum
                 && number <= maximum
            ? number
            : SettingErrors.InvalidValue(key),
        number => number.ToString(CultureInfo.InvariantCulture));
}

public static class SettingErrors
{
    public static Error InvalidValue(string key) =>
        Error.Validation("referential.setting_value_invalid", $"The value of setting '{key}' is invalid.");
}
