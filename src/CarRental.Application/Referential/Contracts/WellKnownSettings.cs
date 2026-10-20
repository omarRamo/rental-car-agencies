using CarRental.Domain.Common;

namespace CarRental.Application.Referential.Contracts;

/// <summary>
/// Business parameters of the platform with their default values (SPEC §4, §7, §8). Stored values are created at
/// startup when missing (never overwritten) and edited later from <c>/admin/settings</c>.
/// </summary>
public static class WellKnownSettings
{
    /// <summary>How long a web request blocks capacity while an agent validates it (blocage, 24 h).</summary>
    public static readonly SettingDefinition<TimeSpan> BookingHoldDuration =
        SettingDefinitions.Duration("bookings.hold_duration", TimeSpan.FromHours(24));

    /// <summary>Preparation buffer added after each return before the vehicle can leave again (2 h).</summary>
    public static readonly SettingDefinition<TimeSpan> PreparationBuffer =
        SettingDefinitions.Duration("fleet.preparation_buffer", TimeSpan.FromHours(2));

    /// <summary>Late return tolerance before an extra rental day is charged (59 min).</summary>
    public static readonly SettingDefinition<TimeSpan> LateReturnTolerance =
        SettingDefinitions.Duration("pricing.late_return_tolerance", TimeSpan.FromMinutes(59));

    /// <summary>Standard VAT rate on rentals (19 %, to be confirmed by the chartered accountant).</summary>
    public static readonly SettingDefinition<decimal> VatRate =
        SettingDefinitions.Number("billing.vat_rate", 0.19m, 0m, 1m);

    /// <summary>Contribution per rented vehicle and per day, excluding VAT (2.000 TND, finance law 2026), part of the VAT base.</summary>
    public static readonly SettingDefinition<Money> DailyContributionExclVat =
        SettingDefinitions.Amount("billing.daily_contribution_excl_vat", Money.Tnd(2m));

    /// <summary>
    /// Stamp duty on invoices. TODO(spec): the amount (barème du droit de timbre) must be provided by the chartered
    /// accountant; 0.000 TND until then (points-ouverts.md, gap 9).
    /// </summary>
    public static readonly SettingDefinition<Money> StampDuty =
        SettingDefinitions.Amount("billing.stamp_duty", Money.Tnd(0m));

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        BookingHoldDuration,
        PreparationBuffer,
        LateReturnTolerance,
        VatRate,
        DailyContributionExclVat,
        StampDuty,
    ];
}
