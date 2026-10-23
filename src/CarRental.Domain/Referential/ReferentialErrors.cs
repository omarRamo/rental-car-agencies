using CarRental.Domain.Common;

namespace CarRental.Domain.Referential;

public static class ReferentialErrors
{
    public static readonly Error AgencyRequired = Error.Validation("referential.agency_required", "An agency is required.");

    public static readonly Error AgencyNameInvalid = Error.Validation(
        "referential.agency_name_invalid",
        $"The agency name is required ({Agency.NameMaxLength} characters max).");

    public static readonly Error AgencyCityInvalid = Error.Validation(
        "referential.agency_city_invalid",
        $"The agency city is required ({Agency.CityMaxLength} characters max).");

    public static readonly Error AgencyFieldTooLong = Error.Validation("referential.agency_field_too_long", "An agency field is too long.");

    public static readonly Error AgencyEmailInvalid = Error.Validation("referential.agency_email_invalid", "The agency e-mail is invalid.");

    public static readonly Error AgencyCoordinatesInvalid = Error.Validation(
        "referential.agency_coordinates_invalid",
        "The agency coordinates need both a latitude (-90 to 90) and a longitude (-180 to 180).");

    public static readonly Error AgencySlugInvalid = Error.Validation(
        "referential.agency_slug_invalid",
        $"The address of the public site has {AgencySlug.MinLength} to {AgencySlug.MaxLength} characters: lower-case letters and digits separated by single hyphens.");

    public static readonly Error AgencySlugReserved = Error.Validation(
        "referential.agency_slug_reserved",
        "This address is used by the platform itself: choose another one.");

    public static readonly Error AgencySlugTaken = Error.Conflict(
        "referential.agency_slug_taken",
        "Another agency already uses this address.");

    public static readonly Error AgencyModulesInvalid = Error.Validation("referential.agency_modules_invalid", "Unknown module.");

    public static readonly Error LocationCodeInvalid = Error.Validation(
        "referential.location_code_invalid",
        $"A location code has {Location.CodeMinLength} to {Location.CodeMaxLength} characters: A-Z, 0-9 or '-'.");

    public static readonly Error LocationNameInvalid = Error.Validation(
        "referential.location_name_invalid",
        $"The location name is required ({Location.NameMaxLength} characters max).");

    public static readonly Error LocationKindInvalid = Error.Validation("referential.location_kind_invalid", "Unknown location kind.");

    public static readonly Error LocationAddressTooLong = Error.Validation("referential.location_address_too_long", "The address is too long.");

    public static readonly Error SettingKeyInvalid = Error.Validation(
        "referential.setting_key_invalid",
        "A setting key is made of lower-case dotted segments, e.g. bookings.hold_duration.");

    public static readonly Error SettingValueInvalid = Error.Validation(
        "referential.setting_value_invalid",
        $"A setting value is required ({Setting.ValueMaxLength} characters max).");

    public static readonly Error PlatformSettingRequired = Error.Validation(
        "referential.platform_setting_required",
        "The platform value of a setting cannot be removed: set it back to its default instead.");
}
