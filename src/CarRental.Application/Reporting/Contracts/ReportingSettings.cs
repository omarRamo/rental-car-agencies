using CarRental.Application.Referential.Contracts;

namespace CarRental.Application.Reporting.Contracts;

/// <summary>Business settings of the Reporting module, declared to the setting catalogue by <c>AddReportingModule()</c>.</summary>
public static class ReportingSettings
{
    /// <summary>
    /// Days during which funnel events are kept before the nightly purge (400: a summer season can be compared with the
    /// previous one; well under the 25 months the CNIL allows for an audience measurement exempted from consent). The
    /// value is stated on the privacy page.
    /// </summary>
    public static readonly SettingDefinition<int> FunnelEventRetentionDays =
        SettingDefinitions.WholeNumber("reporting.funnel_event_retention_days", 400, 30, 3650).PlatformOnly();

    /// <summary>
    /// Days computed again by the nightly snapshot job, ending yesterday (7 by default): a return, a no-show or a cost
    /// recorded a few days late still reaches the snapshots; older days change only when a manager recomputes them.
    /// </summary>
    public static readonly SettingDefinition<int> SnapshotRefreshDays =
        SettingDefinitions.WholeNumber("reporting.snapshot_refresh_days", 7, 1, 31);

    /// <summary>
    /// Months over which a vehicle with a negative margin is flagged "sell or replace?" in the profitability table
    /// (6 by default, SPEC §8: "Décider vente ou remplacement").
    /// </summary>
    public static readonly SettingDefinition<int> SellHintMonths =
        SettingDefinitions.WholeNumber("reporting.sell_hint_months", 6, 1, 24);

    public static IReadOnlyList<SettingDefinition> All { get; } = [FunnelEventRetentionDays, SnapshotRefreshDays, SellHintMonths];
}
