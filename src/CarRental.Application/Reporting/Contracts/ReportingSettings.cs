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
        SettingDefinitions.WholeNumber("reporting.funnel_event_retention_days", 400, 30, 3650);

    public static IReadOnlyList<SettingDefinition> All { get; } = [FunnelEventRetentionDays];
}
