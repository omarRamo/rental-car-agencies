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

    /// <summary>The support registered an agency (ADR-0067).</summary>
    public const string AgencyRegistered = "referential.agency_registered";

    /// <summary>The support changed the name, city or contact details of an agency.</summary>
    public const string AgencyDetailsUpdated = "referential.agency_details_updated";

    /// <summary>The support changed the address of an agency's public site.</summary>
    public const string AgencySlugChanged = "referential.agency_slug_changed";

    /// <summary>The support switched modules of an agency on or off.</summary>
    public const string AgencyModulesChanged = "referential.agency_modules_changed";

    /// <summary>The support suspended an agency or lifted its suspension.</summary>
    public const string AgencyActivationChanged = "referential.agency_activation_changed";

    /// <summary>A pick-up / return location was added to an agency.</summary>
    public const string LocationCreated = "referential.location_created";

    /// <summary>The name, kind or address of a location changed.</summary>
    public const string LocationUpdated = "referential.location_updated";

    /// <summary>A location was deactivated (no longer offered for new bookings) or reactivated.</summary>
    public const string LocationActivationChanged = "referential.location_activation_changed";
}
