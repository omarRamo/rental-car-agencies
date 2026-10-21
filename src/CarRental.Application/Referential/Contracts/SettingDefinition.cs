using System.Globalization;
using System.Text.RegularExpressions;
using CarRental.Domain.Common;

namespace CarRental.Application.Referential.Contracts;

/// <summary>
/// A typed business parameter stored as an invariant string in the Referential module (<c>referential.Settings</c>).
/// Modules declare their own definitions (e.g. in <c>{Module}/Contracts</c>) and read them through <see cref="ISettingsReader"/>;
/// the default applies until a value is stored.
/// </summary>
/// <remarks>
/// The settings screen (<c>/admin/settings</c>) edits every declared definition with the editor of its
/// <see cref="Editor"/>; its label and help text are the resources <c>Setting.{key}.Label</c> and
/// <c>Setting.{key}.Help</c> of <c>Resources/Admin/Settings/SettingLabels(.en).resx</c> (an architecture test requires the
/// label in French and English).
/// </remarks>
public abstract partial class SettingDefinition
{
    /// <summary>Same limit as the stored setting (<c>referential.Settings.Key</c>).</summary>
    public const int KeyMaxLength = 100;

    protected SettingDefinition(string key, string defaultValue, SettingEditor editor, SettingScope scope = SettingScope.Agency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(defaultValue);
        ArgumentNullException.ThrowIfNull(editor);

        // Same rule as the stored setting (Setting.Create): the contracts may not use the domain types (test).
        if (key.Length > KeyMaxLength || !KeyPattern().IsMatch(key))
        {
            throw new ArgumentException($"'{key}' is not a setting key: lower-case dotted segments such as bookings.hold_duration.", nameof(key));
        }

        Key = key;
        DefaultValue = defaultValue;
        Editor = editor;
        Scope = scope;
    }

    /// <summary>Dotted lower-case key, e.g. <c>bookings.hold_duration</c>; its first segment groups it on the settings screen.</summary>
    public string Key { get; }

    /// <summary>Default value, in the invariant storage format.</summary>
    public string DefaultValue { get; }

    /// <summary>How the settings screen edits the value.</summary>
    public SettingEditor Editor { get; }

    /// <summary>Whether an agency may have its own value (default), or the platform value applies everywhere.</summary>
    public SettingScope Scope { get; }

    /// <summary>The group of the setting on the settings screen: the first segment of its key (<c>bookings</c>).</summary>
    public string Group => Key[..Key.IndexOf('.', StringComparison.Ordinal)];

    /// <summary>Checks that <paramref name="value"/> is a valid stored value for this setting.</summary>
    public abstract Result Validate(string? value);

    public override string ToString() => Key;

    [GeneratedRegex("^[a-z][a-z0-9_]*([.][a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>A setting of type <typeparamref name="T"/>.</summary>
public sealed class SettingDefinition<T> : SettingDefinition
{
    private readonly Func<string, Result<T>> _parse;
    private readonly Func<T, string> _format;

    /// <param name="key">Dotted lower-case key.</param>
    /// <param name="defaultValue">Value used until one is stored.</param>
    /// <param name="parse">Parses and validates a stored value.</param>
    /// <param name="format">Formats a value to its invariant storage text.</param>
    /// <param name="editor">The editor of the settings screen; inferred from <typeparamref name="T"/> when omitted.</param>
    /// <param name="scope">Whether an agency may override the platform value.</param>
    public SettingDefinition(
        string key,
        T defaultValue,
        Func<string, Result<T>> parse,
        Func<T, string> format,
        SettingEditor? editor = null,
        SettingScope scope = SettingScope.Agency)
        : base(key, (format ?? throw new ArgumentNullException(nameof(format)))(defaultValue), editor ?? SettingEditor.For(typeof(T)), scope)
    {
        ArgumentNullException.ThrowIfNull(parse);
        Default = defaultValue;
        _parse = parse;
        _format = format;
    }

    public T Default { get; }

    /// <summary>The same setting with a single value for the whole platform (read without agency; no agency value).</summary>
    public SettingDefinition<T> PlatformOnly() => new(Key, Default, _parse, _format, Editor, SettingScope.Platform);

    public Result<T> Parse(string? value) =>
        value is null ? SettingErrors.InvalidValue(Key) : _parse(value);

    public string Format(T value) => _format(value);

    public override Result Validate(string? value)
    {
        var parsed = Parse(value);
        return parsed.IsSuccess ? Result.Success() : parsed.Error;
    }
}

/// <summary>Where a setting takes its value.</summary>
public enum SettingScope
{
    /// <summary>The platform value, which an agency may replace by its own (read with the agency).</summary>
    Agency = 0,

    /// <summary>One value for the whole platform (technical or national rules, read without agency).</summary>
    Platform = 1,
}

/// <summary>Kind of editor of a setting on the settings screen.</summary>
public enum SettingKind
{
    /// <summary>Free text (<see cref="SettingEditor.MaxLength"/>).</summary>
    Text = 0,

    /// <summary>A duration (<see cref="TimeSpan"/>, constant format <c>1.00:00:00</c>), edited in minutes, hours or days.</summary>
    Duration = 1,

    /// <summary>A decimal number (rate, ratio), invariant dot.</summary>
    Number = 2,

    /// <summary>An integer.</summary>
    WholeNumber = 3,

    /// <summary>An amount in TND with three decimals (millimes).</summary>
    Amount = 4,

    /// <summary>A yes/no switch (<c>true</c> / <c>false</c>).</summary>
    Boolean = 5,

    /// <summary>A time range within a day (<c>08:00-18:00</c>).</summary>
    TimeRange = 6,
}

/// <summary>
/// How the settings screen edits a setting: the kind of editor and its bounds, in the storage format of the value
/// (they are hints for the editor: <see cref="SettingDefinition.Validate"/> decides).
/// </summary>
public sealed record SettingEditor(SettingKind Kind, string? Minimum = null, string? Maximum = null, int? MaxLength = null)
{
    /// <summary>The editor of a value type: duration, decimal number, integer, amount, boolean, time range, otherwise text.</summary>
    public static SettingEditor For(Type valueType)
    {
        ArgumentNullException.ThrowIfNull(valueType);
        return new SettingEditor(valueType switch
        {
            _ when valueType == typeof(TimeSpan) => SettingKind.Duration,
            _ when valueType == typeof(decimal) => SettingKind.Number,
            _ when valueType == typeof(int) => SettingKind.WholeNumber,
            _ when valueType == typeof(Money) => SettingKind.Amount,
            _ when valueType == typeof(bool) => SettingKind.Boolean,
            _ when valueType == typeof(DailyTimeRange) => SettingKind.TimeRange,
            _ => SettingKind.Text,
        });
    }
}

/// <summary>A time range within a day, from <see cref="Start"/> (included) to <see cref="End"/> (excluded), e.g. opening hours.</summary>
public readonly record struct DailyTimeRange(TimeOnly Start, TimeOnly End)
{
    /// <summary>True when <paramref name="time"/> is in <c>[Start, End)</c>.</summary>
    public bool Contains(TimeOnly time) => time >= Start && time < End;

    /// <summary>Storage format: <c>08:00-18:00</c>.</summary>
    public override string ToString() => SettingValues.FormatTimeRange(this);
}

/// <summary>
/// The invariant storage formats of the usual setting types, shared by the definitions and the settings screen (which
/// edits a typed value and stores its formatted text).
/// </summary>
public static class SettingValues
{
    private const string TimeFormat = "HH:mm";

    public static string FormatDuration(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

    public static bool TryParseDuration(string? value, out TimeSpan duration) =>
        TimeSpan.TryParseExact(value, "c", CultureInfo.InvariantCulture, out duration);

    public static string FormatNumber(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseNumber(string? value, out decimal number) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out number);

    public static string FormatWholeNumber(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseWholeNumber(string? value, out int number) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);

    public static string FormatAmount(decimal amount) => amount.ToString("F3", CultureInfo.InvariantCulture);

    public static string FormatBoolean(bool value) => value ? "true" : "false";

    public static bool TryParseBoolean(string? value, out bool flag) => bool.TryParse(value, out flag);

    public static string FormatTimeRange(DailyTimeRange range) =>
        string.Create(CultureInfo.InvariantCulture, $"{range.Start.ToString(TimeFormat, CultureInfo.InvariantCulture)}-{range.End.ToString(TimeFormat, CultureInfo.InvariantCulture)}");

    /// <summary>Parses <c>HH:mm-HH:mm</c> with a start strictly before the end.</summary>
    public static bool TryParseTimeRange(string? value, out DailyTimeRange range)
    {
        range = default;
        var parts = value?.Split('-');
        if (parts is not { Length: 2 }
            || !TimeOnly.TryParseExact(parts[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(parts[1], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            || start >= end)
        {
            return false;
        }

        range = new DailyTimeRange(start, end);
        return true;
    }
}

/// <summary>Factories for the usual setting types, with invariant storage formats.</summary>
public static class SettingDefinitions
{
    /// <summary>Upper bound of a duration setting, and of a duration without explicit bounds.</summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(365);

    /// <summary>A duration stored with the TimeSpan constant format (<c>1.00:00:00</c> for 24 h), between zero and 365 days.</summary>
    public static SettingDefinition<TimeSpan> Duration(string key, TimeSpan defaultValue) =>
        Duration(key, defaultValue, TimeSpan.Zero, MaximumDuration);

    /// <summary>A duration stored with the TimeSpan constant format, within [<paramref name="minimum"/>, <paramref name="maximum"/>].</summary>
    public static SettingDefinition<TimeSpan> Duration(string key, TimeSpan defaultValue, TimeSpan minimum, TimeSpan maximum)
    {
        CheckBounds(defaultValue, minimum, maximum);
        if (minimum < TimeSpan.Zero || maximum > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), "A duration setting lies between zero and 365 days.");
        }

        return new(
            key,
            defaultValue,
            value => SettingValues.TryParseDuration(value, out var duration) && duration >= minimum && duration <= maximum
                ? duration
                : SettingErrors.InvalidValue(key),
            SettingValues.FormatDuration,
            new SettingEditor(SettingKind.Duration, SettingValues.FormatDuration(minimum), SettingValues.FormatDuration(maximum)));
    }

    /// <summary>A decimal (rate, ratio) stored with an invariant dot, within [<paramref name="minimum"/>, <paramref name="maximum"/>].</summary>
    public static SettingDefinition<decimal> Number(string key, decimal defaultValue, decimal minimum, decimal maximum)
    {
        CheckBounds(defaultValue, minimum, maximum);
        return new(
            key,
            defaultValue,
            value => SettingValues.TryParseNumber(value, out var number) && number >= minimum && number <= maximum
                ? number
                : SettingErrors.InvalidValue(key),
            SettingValues.FormatNumber,
            new SettingEditor(SettingKind.Number, SettingValues.FormatNumber(minimum), SettingValues.FormatNumber(maximum)));
    }

    /// <summary>An amount in TND with at most three decimals (millimes), stored like <c>2.000</c>; negative amounts are rejected.</summary>
    public static SettingDefinition<Money> Amount(string key, Money defaultValue) => new(
        key,
        defaultValue,
        value => SettingValues.TryParseNumber(value, out var number)
                 && number >= 0m
                 && Money.FromExact(number, Currency.Tnd) is { IsSuccess: true } money
            ? money.Value
            : SettingErrors.InvalidValue(key),
        money => SettingValues.FormatAmount(money.Amount),
        new SettingEditor(SettingKind.Amount, SettingValues.FormatAmount(0m)));

    public static SettingDefinition<bool> Boolean(string key, bool defaultValue) => new(
        key,
        defaultValue,
        value => SettingValues.TryParseBoolean(value, out var flag) ? flag : SettingErrors.InvalidValue(key),
        SettingValues.FormatBoolean,
        new SettingEditor(SettingKind.Boolean));

    public static SettingDefinition<int> WholeNumber(string key, int defaultValue, int minimum, int maximum)
    {
        CheckBounds(defaultValue, minimum, maximum);
        return new(
            key,
            defaultValue,
            value => SettingValues.TryParseWholeNumber(value, out var number) && number >= minimum && number <= maximum
                ? number
                : SettingErrors.InvalidValue(key),
            SettingValues.FormatWholeNumber,
            new SettingEditor(SettingKind.WholeNumber, SettingValues.FormatWholeNumber(minimum), SettingValues.FormatWholeNumber(maximum)));
    }

    /// <summary>A text of at most <paramref name="maxLength"/> characters, without control characters (may be empty).</summary>
    public static SettingDefinition<string> Text(string key, string defaultValue, int maxLength = 200)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        if (defaultValue.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultValue), "The default text is longer than the maximum length.");
        }

        return new(
            key,
            defaultValue,
            value => value.Length <= maxLength && !value.Any(char.IsControl) ? value : SettingErrors.InvalidValue(key),
            value => value,
            new SettingEditor(SettingKind.Text, MaxLength: maxLength));
    }

    /// <summary>A time range within a day stored like <c>08:00-18:00</c> (start strictly before end).</summary>
    public static SettingDefinition<DailyTimeRange> TimeRange(string key, DailyTimeRange defaultValue)
    {
        if (defaultValue.Start >= defaultValue.End)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultValue), "A time range starts strictly before its end.");
        }

        return new(
            key,
            defaultValue,
            value => SettingValues.TryParseTimeRange(value, out var range) ? range : SettingErrors.InvalidValue(key),
            SettingValues.FormatTimeRange,
            new SettingEditor(SettingKind.TimeRange));
    }

    private static void CheckBounds<T>(T defaultValue, T minimum, T maximum)
        where T : IComparable<T>
    {
        if (minimum.CompareTo(maximum) > 0 || defaultValue.CompareTo(minimum) < 0 || defaultValue.CompareTo(maximum) > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultValue), "The default value of a setting lies within its bounds.");
        }
    }
}

public static class SettingErrors
{
    public static Error InvalidValue(string key) =>
        Error.Validation("referential.setting_value_invalid", $"The value of setting '{key}' is invalid.");

    /// <summary>The setting is not declared by any module.</summary>
    public static Error Unknown(string key) =>
        Error.NotFound("referential.setting_unknown", $"No setting is declared with the key '{key}'.");

    public static readonly Error Forbidden = Error.Forbidden("referential.forbidden", "The current user may not manage the settings.");

    public static readonly Error AgencyNotFound = Error.NotFound("referential.agency_not_found", "The agency does not exist.");

    public static readonly Error PlatformOnly = Error.Validation(
        "referential.setting_platform_only",
        "This setting has a single value for the whole platform: an agency cannot have its own.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "referential.setting_concurrent_update",
        "The setting was changed meanwhile: reload it and try again.");
}
