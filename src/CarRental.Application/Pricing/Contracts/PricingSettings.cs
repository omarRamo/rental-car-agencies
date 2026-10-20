using System.Globalization;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Pricing.Contracts;

/// <summary>
/// Business settings declared by the Pricing module (docs/conception/phase-1-exploitation.md §1), registered in the
/// setting catalogue by <c>AddPricingModule()</c>. The engine also reads <see cref="WellKnownSettings.VatRate"/>,
/// <see cref="WellKnownSettings.DailyContributionExclVat"/> and <see cref="WellKnownSettings.LateReturnTolerance"/>.
/// </summary>
public static class PricingSettings
{
    /// <summary>Round the published daily prices to the dinar, after the channel rule (true).</summary>
    public static readonly SettingDefinition<bool> RoundPublicPricesToDinar =
        SettingDefinitions.Boolean("pricing.round_public_prices_to_dinar", true);

    /// <summary>A driver younger than this age at pick-up pays the young driver extra (25).</summary>
    public static readonly SettingDefinition<int> YoungDriverAgeBelow =
        SettingDefinitions.WholeNumber("drivers.young_driver_age_below", 25, 18, 99);

    /// <summary>
    /// Opening hours of the agency, Tunis time, stored as <c>HH:mm-HH:mm</c> (<c>08:00-20:00</c>): a pick-up or a return
    /// before the opening or after the closing time pays the after-hours extra. Both bounds are inside the opening hours.
    /// </summary>
    public static readonly SettingDefinition<OpeningHours> AgencyOpeningHours = new(
        "pricing.opening_hours",
        new OpeningHours(new TimeOnly(8, 0), new TimeOnly(20, 0)),
        value => OpeningHours.Parse(value) is { IsSuccess: true } parsed ? parsed.Value : SettingErrors.InvalidValue("pricing.opening_hours"),
        hours => hours.ToString());

    public static IReadOnlyList<SettingDefinition> All { get; } = [RoundPublicPricesToDinar, YoungDriverAgeBelow, AgencyOpeningHours];
}

/// <summary>Daily opening hours of an agency (Tunis time); <see cref="OpensAt"/> is before <see cref="ClosesAt"/>.</summary>
public sealed record OpeningHours(TimeOnly OpensAt, TimeOnly ClosesAt)
{
    private const string TimeFormat = "HH:mm";

    /// <summary>Parses <c>HH:mm-HH:mm</c> (24-hour clock).</summary>
    public static Result<OpeningHours> Parse(string? value)
    {
        var parts = value?.Split('-', StringSplitOptions.TrimEntries) ?? [];
        if (parts.Length == 2
            && TimeOnly.TryParseExact(parts[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var opensAt)
            && TimeOnly.TryParseExact(parts[1], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var closesAt)
            && opensAt < closesAt)
        {
            return new OpeningHours(opensAt, closesAt);
        }

        return SettingErrors.InvalidValue("pricing.opening_hours");
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{OpensAt.ToString(TimeFormat, CultureInfo.InvariantCulture)}-{ClosesAt.ToString(TimeFormat, CultureInfo.InvariantCulture)}");
}
