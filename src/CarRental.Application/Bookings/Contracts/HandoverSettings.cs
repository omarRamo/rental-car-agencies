using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Contracts;

/// <summary>
/// Settings of the departure and return (ADR-0042), per agency, all amounts VAT included. The late return tolerance is
/// the one of the pricing engine (<see cref="WellKnownSettings.LateReturnTolerance"/>), the VAT rate the platform one
/// (<see cref="WellKnownSettings.VatRate"/>), the franchise the deposit of the quote (reduced with the waiver).
/// </summary>
public static class HandoverSettings
{
    /// <summary>Kilometres included per charged day; 0 means unlimited mileage (the default).</summary>
    public static readonly SettingDefinition<int> MileageIncludedPerDay =
        SettingDefinitions.WholeNumber("bookings.mileage_included_per_day_km", 0, 0, 5_000);

    /// <summary>Price of a kilometre beyond the included mileage (0.300 TND).</summary>
    public static readonly SettingDefinition<Money> ExtraKilometrePrice =
        SettingDefinitions.Amount("bookings.extra_km_price", Money.Tnd(0.3m));

    /// <summary>Full-to-full fuel policy: fee per missing eighth of tank (15.000 TND, fuel and service).</summary>
    public static readonly SettingDefinition<Money> RefuelFeePerEighth =
        SettingDefinitions.Amount("bookings.refuel_fee_per_eighth", Money.Tnd(15m));

    /// <summary>Fee when the vehicle comes back dirty (30.000 TND).</summary>
    public static readonly SettingDefinition<Money> CleaningFee =
        SettingDefinitions.Amount("bookings.cleaning_fee", Money.Tnd(30m));

    public static IReadOnlyList<SettingDefinition> All { get; } = [MileageIncludedPerDay, ExtraKilometrePrice, RefuelFeePerEighth, CleaningFee];
}
