using CarRental.Application.Referential.Contracts;

namespace CarRental.Application.Fleet.Contracts;

/// <summary>
/// Business settings of the Fleet module (the preparation buffer <c>fleet.preparation_buffer</c> is declared with the
/// platform settings in <see cref="WellKnownSettings"/>, since bookings use it).
/// </summary>
public static class FleetSettings
{
    /// <summary>A planned maintenance is reported "due soon" this many kilometres before its due distance (1 000 km).</summary>
    public static readonly SettingDefinition<int> MaintenanceDueSoonDistance =
        SettingDefinitions.WholeNumber("fleet.maintenance_due_soon_km", 1000, 0, 50_000);

    /// <summary>A planned maintenance is reported "due soon" this many days before its due date (30 days).</summary>
    public static readonly SettingDefinition<int> MaintenanceDueSoonDays =
        SettingDefinitions.WholeNumber("fleet.maintenance_due_soon_days", 30, 0, 365);

    public static IReadOnlyList<SettingDefinition> All { get; } = [MaintenanceDueSoonDistance, MaintenanceDueSoonDays];
}
