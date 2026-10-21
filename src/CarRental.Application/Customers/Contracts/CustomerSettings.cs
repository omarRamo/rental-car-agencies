using CarRental.Application.Referential.Contracts;

namespace CarRental.Application.Customers.Contracts;

/// <summary>
/// Business settings owned by the Customers module (conception §1), declared to the Referential catalogue by
/// <c>AddCustomersModule()</c>. The young driver threshold belongs to Pricing.
/// </summary>
public static class CustomerSettings
{
    /// <summary>Minimum age of a driver on the pick-up date (21, usual in Tunisia).</summary>
    public static readonly SettingDefinition<int> DriverMinimumAge =
        SettingDefinitions.WholeNumber("drivers.minimum_age", 21, 16, 99);

    /// <summary>Minimum seniority of the driving licence on the pick-up date, in years (2).</summary>
    public static readonly SettingDefinition<int> DriverMinimumLicenseYears =
        SettingDefinitions.WholeNumber("drivers.minimum_license_years", 2, 0, 50);

    /// <summary>
    /// Automatic purge of identity document images after the retention period. Off by default: TODO(spec) the legal
    /// destruction procedure (law 2004-63 art. 45: report by a bailiff, expert appointed by the INPDP) is not validated.
    /// </summary>
    public static readonly SettingDefinition<bool> DocumentImageRetentionEnabled =
        SettingDefinitions.Boolean("customers.document_image_retention_enabled", false).PlatformOnly();

    /// <summary>
    /// Days during which document images are kept after the last closed rental of the person (or after their upload):
    /// 400 by default, i.e. a year for traffic fines to arrive plus a margin.
    /// </summary>
    public static readonly SettingDefinition<int> DocumentImageRetentionDays =
        SettingDefinitions.WholeNumber("customers.document_image_retention_days", 400, 30, 3650).PlatformOnly();

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        DriverMinimumAge,
        DriverMinimumLicenseYears,
        DocumentImageRetentionEnabled,
        DocumentImageRetentionDays,
    ];
}
