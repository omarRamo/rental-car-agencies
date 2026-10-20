using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>
/// Driver eligibility rules (SPEC §5 "Éligibilité", conception §6), evaluated on the Africa/Tunis dates of the rental:
/// minimum age and licence seniority on the pick-up date, identity document by nationality (CIN for a Tunisian,
/// passport otherwise), every required document valid until the return date, and an international driving permit
/// with a licence not written in latin script. Pure: the caller loads the person and the settings.
/// </summary>
/// <remarks>The blacklist is deliberately not an eligibility issue: issues may be shown to the applicant, the blacklist never.</remarks>
public static class DriverEligibility
{
    public static DriverEligibilityReport Evaluate(Person driver, Period rental, EligibilityRules rules)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(rental);
        ArgumentNullException.ThrowIfNull(rules);

        var pickupDate = TunisTime.DateOf(rental.Start);
        var returnDate = TunisTime.DateOf(rental.End);
        var issues = new List<EligibilityIssue>();

        CheckAge(driver, pickupDate, rules, issues);
        CheckIdentityDocument(driver, returnDate, issues);
        CheckLicense(driver, pickupDate, returnDate, rules, issues);

        return new DriverEligibilityReport(issues);
    }

    /// <summary>Whole years elapsed from <paramref name="from"/> to <paramref name="on"/> (an anniversary on 29 February falls on 28 February in common years).</summary>
    public static int CompletedYears(DateOnly from, DateOnly on)
    {
        var years = on.Year - from.Year;
        if (on < from.AddYears(years))
        {
            years--;
        }

        return years;
    }

    private static void CheckAge(Person driver, DateOnly pickupDate, EligibilityRules rules, List<EligibilityIssue> issues)
    {
        if (driver.BirthDate is not { } birthDate)
        {
            issues.Add(new EligibilityIssue(EligibilityIssueKind.BirthDateMissing));
            return;
        }

        var age = CompletedYears(birthDate, pickupDate);
        if (age < rules.MinimumAge)
        {
            issues.Add(new EligibilityIssue(EligibilityIssueKind.UnderMinimumAge, Required: rules.MinimumAge, Actual: age));
        }
    }

    private static void CheckIdentityDocument(Person driver, DateOnly returnDate, List<EligibilityIssue> issues)
    {
        if (driver.Nationality is not { } nationality)
        {
            issues.Add(new EligibilityIssue(EligibilityIssueKind.NationalityMissing));
            return;
        }

        var isTunisian = string.Equals(nationality, CountryCodes.Tunisia, StringComparison.Ordinal);
        var required = isTunisian ? IdentityDocumentType.NationalId : IdentityDocumentType.Passport;
        var document = driver.CurrentDocument(required);
        if (document is null || (isTunisian && !string.Equals(document.IssuingCountry, CountryCodes.Tunisia, StringComparison.Ordinal)))
        {
            issues.Add(new EligibilityIssue(EligibilityIssueKind.IdentityDocumentMissing, DocumentType: required));
            return;
        }

        CheckValidity(document, returnDate, issues);
    }

    private static void CheckLicense(Person driver, DateOnly pickupDate, DateOnly returnDate, EligibilityRules rules, List<EligibilityIssue> issues)
    {
        var license = driver.CurrentDocument(IdentityDocumentType.DrivingLicense);
        if (license is null)
        {
            issues.Add(new EligibilityIssue(EligibilityIssueKind.DrivingLicenseMissing, DocumentType: IdentityDocumentType.DrivingLicense));
            return;
        }

        var years = license.IssuedOn is { } issuedOn ? CompletedYears(issuedOn, pickupDate) : 0;
        if (years < rules.MinimumLicenseYears)
        {
            issues.Add(new EligibilityIssue(
                EligibilityIssueKind.LicenseHeldTooShort,
                DocumentType: IdentityDocumentType.DrivingLicense,
                Date: license.IssuedOn,
                Required: rules.MinimumLicenseYears,
                Actual: years));
        }

        CheckValidity(license, returnDate, issues);

        if (license.IsLatinScript)
        {
            return;
        }

        var permit = driver.CurrentDocument(IdentityDocumentType.InternationalDrivingPermit);
        if (permit is null)
        {
            issues.Add(new EligibilityIssue(EligibilityIssueKind.InternationalPermitMissing, DocumentType: IdentityDocumentType.InternationalDrivingPermit));
            return;
        }

        CheckValidity(permit, returnDate, issues);
    }

    private static void CheckValidity(IdentityDocument document, DateOnly returnDate, List<EligibilityIssue> issues)
    {
        if (!document.IsValidUntil(returnDate))
        {
            issues.Add(new EligibilityIssue(EligibilityIssueKind.DocumentExpiresBeforeReturn, DocumentType: document.Type, Date: document.ExpiresOn));
        }
    }
}

/// <summary>Thresholds read from the settings <c>drivers.minimum_age</c> and <c>drivers.minimum_license_years</c>.</summary>
public sealed record EligibilityRules(int MinimumAge, int MinimumLicenseYears)
{
    public int MinimumAge { get; } = MinimumAge >= 0
        ? MinimumAge
        : throw new ArgumentOutOfRangeException(nameof(MinimumAge), MinimumAge, "The minimum age cannot be negative.");

    public int MinimumLicenseYears { get; } = MinimumLicenseYears >= 0
        ? MinimumLicenseYears
        : throw new ArgumentOutOfRangeException(nameof(MinimumLicenseYears), MinimumLicenseYears, "The licence seniority cannot be negative.");
}

/// <summary>Why a driver is not eligible.</summary>
public enum EligibilityIssueKind
{
    /// <summary>The birth date is unknown: the age cannot be checked.</summary>
    BirthDateMissing = 1,

    /// <summary>Younger than <c>drivers.minimum_age</c> on the pick-up date.</summary>
    UnderMinimumAge = 2,

    /// <summary>The nationality is unknown: the required identity document cannot be determined.</summary>
    NationalityMissing = 3,

    /// <summary>No current CIN (Tunisian) or passport (foreigner).</summary>
    IdentityDocumentMissing = 4,

    DrivingLicenseMissing = 5,

    /// <summary>Licence held for less than <c>drivers.minimum_license_years</c> on the pick-up date.</summary>
    LicenseHeldTooShort = 6,

    /// <summary>A required document expires before the return date.</summary>
    DocumentExpiresBeforeReturn = 7,

    /// <summary>The licence is not in latin script and no international driving permit is on file.</summary>
    InternationalPermitMissing = 8,
}

/// <summary>One eligibility issue, with the document concerned, a date (expiry, issue) and the required and actual values.</summary>
public sealed record EligibilityIssue(
    EligibilityIssueKind Kind,
    IdentityDocumentType? DocumentType = null,
    DateOnly? Date = null,
    int? Required = null,
    int? Actual = null);

/// <summary>Result of <see cref="DriverEligibility.Evaluate"/>: eligible when there is no issue.</summary>
public sealed class DriverEligibilityReport(IReadOnlyList<EligibilityIssue> issues)
{
    public IReadOnlyList<EligibilityIssue> Issues { get; } = issues;

    public bool IsEligible => Issues.Count == 0;
}
