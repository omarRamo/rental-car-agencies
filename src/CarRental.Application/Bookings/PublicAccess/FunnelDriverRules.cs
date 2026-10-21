using System.Globalization;
using CarRental.Application.Customers.Contracts;

namespace CarRental.Application.Bookings.PublicAccess;

/// <summary>
/// The fields of step 2 (ADR-0050): what the funnel requires (names, nationality, birth date, licence, identity
/// document — CIN for a Tunisian, passport otherwise —, international permit with a licence not in latin script,
/// phone and e-mail of the main driver), parsed into what the Customers module validates and evaluates. Field errors
/// are keyed <c>{driver}.{field}</c>.
/// </summary>
internal static class FunnelDriverRules
{
    public const string Required = "funnel.required";
    public const string DateInvalid = "funnel.date_invalid";
    public const string Tunisia = "TN";

    public const string FirstName = "first_name";
    public const string LastName = "last_name";
    public const string Nationality = "nationality";
    public const string BirthDate = "birth_date";
    public const string Phone = "phone";
    public const string Email = "email";
    public const string LicenseNumber = "license_number";
    public const string LicenseIssuedOn = "license_issued_on";
    public const string LicenseExpiresOn = "license_expires_on";
    public const string LicenseCountry = "license_country";
    public const string IdentityNumber = "identity_number";
    public const string IdentityCountry = "identity_country";
    public const string IdentityExpiresOn = "identity_expires_on";
    public const string PermitNumber = "permit_number";
    public const string PermitCountry = "permit_country";
    public const string PermitExpiresOn = "permit_expires_on";

    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>The identity document required by a nationality: CIN for a Tunisian, passport otherwise.</summary>
    public static IdentityDocumentTypeInfo IdentityTypeFor(string? nationality) =>
        string.Equals(nationality?.Trim(), Tunisia, StringComparison.OrdinalIgnoreCase) ? IdentityDocumentTypeInfo.NationalId : IdentityDocumentTypeInfo.Passport;

    /// <summary>
    /// The driver typed in <paramref name="input"/> for <paramref name="role"/> (<c>main</c>, <c>second</c>), or
    /// <see langword="null"/> with the missing or malformed fields added to <paramref name="errors"/>.
    /// </summary>
    public static SubmittedDriverDetails? Parse(FunnelDriverInput input, string role, bool contactRequired, List<FunnelFieldError> errors)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(errors);
        var before = errors.Count;

        string? Text(string? value, string field, bool required)
        {
            var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (text is null && required)
            {
                errors.Add(new FunnelFieldError($"{role}.{field}", Required));
            }

            return text;
        }

        DateOnly? Date(string? value, string field, bool required)
        {
            var text = Text(value, field, required);
            if (text is null)
            {
                return null;
            }

            if (DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }

            errors.Add(new FunnelFieldError($"{role}.{field}", DateInvalid));
            return null;
        }

        var firstName = Text(input.FirstName, FirstName, required: true);
        var lastName = Text(input.LastName, LastName, required: true);
        var nationality = Text(input.Nationality, Nationality, required: true)?.ToUpperInvariant();
        var birthDate = Date(input.BirthDate, BirthDate, required: true);
        var phone = Text(input.Phone, Phone, contactRequired);
        var email = Text(input.Email, Email, contactRequired);

        var licenseNumber = Text(input.LicenseNumber, LicenseNumber, required: true);
        var licenseIssuedOn = Date(input.LicenseIssuedOn, LicenseIssuedOn, required: true);
        var licenseExpiresOn = Date(input.LicenseExpiresOn, LicenseExpiresOn, required: false);
        var licenseCountry = Text(input.LicenseCountry, LicenseCountry, required: true)?.ToUpperInvariant();

        var identityType = IdentityTypeFor(nationality);
        var identityNumber = Text(input.IdentityNumber, IdentityNumber, required: true);
        var identityCountry = identityType == IdentityDocumentTypeInfo.NationalId
            ? Tunisia
            : Text(input.IdentityCountry, IdentityCountry, required: false)?.ToUpperInvariant() ?? nationality;
        var identityExpiresOn = Date(input.IdentityExpiresOn, IdentityExpiresOn, required: identityType == IdentityDocumentTypeInfo.Passport);

        string? permitNumber = null;
        string? permitCountry = null;
        DateOnly? permitExpiresOn = null;
        if (!input.LicenseLatinScript)
        {
            permitNumber = Text(input.PermitNumber, PermitNumber, required: true);
            permitCountry = Text(input.PermitCountry, PermitCountry, required: true)?.ToUpperInvariant();
            permitExpiresOn = Date(input.PermitExpiresOn, PermitExpiresOn, required: true);
        }

        if (errors.Count > before)
        {
            return null;
        }

        List<IdentityDocumentInput> documents =
        [
            new(identityType, identityNumber!, identityCountry!, ExpiresOn: identityExpiresOn),
            new(IdentityDocumentTypeInfo.DrivingLicense, licenseNumber!, licenseCountry!, licenseIssuedOn, licenseExpiresOn, input.LicenseLatinScript),
        ];
        if (!input.LicenseLatinScript)
        {
            documents.Add(new IdentityDocumentInput(IdentityDocumentTypeInfo.InternationalDrivingPermit, permitNumber!, permitCountry!, ExpiresOn: permitExpiresOn));
        }

        return new SubmittedDriverDetails(firstName!, lastName!, nationality, birthDate, phone, email, documents);
    }

    /// <summary>The field an error of the Customers module is about (<see langword="null"/> document: the details).</summary>
    public static string FieldFor(string role, SubmittedDriverError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var prefix = error.DocumentType switch
        {
            IdentityDocumentTypeInfo.NationalId or IdentityDocumentTypeInfo.Passport => "identity",
            IdentityDocumentTypeInfo.DrivingLicense => "license",
            IdentityDocumentTypeInfo.InternationalDrivingPermit => "permit",
            _ => null,
        };
        if (prefix is null)
        {
            var field = error.Code switch
            {
                "customers.person.first_name_invalid" => FirstName,
                "customers.person.last_name_invalid" => LastName,
                "customers.country_invalid" => Nationality,
                "customers.person.birth_date_invalid" => BirthDate,
                "customers.phone_invalid" => Phone,
                "customers.email_invalid" => Email,
                _ => FunnelFields.Eligibility,
            };
            return $"{role}.{field}";
        }

        var suffix = error.Code switch
        {
            "customers.country_invalid" => "country",
            "customers.document.issue_date_required" or "customers.document.dates_invalid" => prefix == "identity" ? "expires_on" : "issued_on",
            "customers.document.expiry_date_required" => "expires_on",
            _ => "number",
        };
        return $"{role}.{prefix}_{suffix}";
    }

    /// <summary>
    /// The text key (<c>funnel.eligibility.{kind}</c>) and arguments of an eligibility issue, attached to the driver:
    /// the document type first when there is one, then the required value, the actual value and the date.
    /// </summary>
    public static FunnelFieldError ErrorFor(string role, EligibilityIssueInfo issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var arguments = new List<string>();
        if (issue.DocumentType is { } type)
        {
            arguments.Add(type.ToString());
        }

        if (issue.Required is { } required)
        {
            arguments.Add(required.ToString(CultureInfo.InvariantCulture));
        }

        if (issue.Actual is { } actual)
        {
            arguments.Add(actual.ToString(CultureInfo.InvariantCulture));
        }

        if (issue.Date is { } date)
        {
            arguments.Add(date.ToString(DateFormat, CultureInfo.InvariantCulture));
        }

        return new FunnelFieldError($"{role}.{FunnelFields.Eligibility}", $"funnel.eligibility.{issue.Kind}", arguments);
    }
}
