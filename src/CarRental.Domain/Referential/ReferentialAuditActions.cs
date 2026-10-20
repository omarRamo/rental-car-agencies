namespace CarRental.Domain.Referential;

/// <summary>
/// Explicit audit events of the Referential module (dotted, lower case, never renamed). The changed rows are also
/// audited automatically; these events name the setting and the scope in a readable way.
/// </summary>
public static class ReferentialAuditActions
{
    /// <summary>A platform value or an agency value of a setting was set.</summary>
    public const string SettingChanged = "referential.setting_changed";

    /// <summary>A platform value went back to its default, or an agency value was removed (the platform value applies).</summary>
    public const string SettingReset = "referential.setting_reset";
}
