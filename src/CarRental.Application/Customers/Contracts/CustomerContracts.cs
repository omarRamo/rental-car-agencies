namespace CarRental.Application.Customers.Contracts;

/// <summary>A person found by a search: enough to recognize and pick the right one.</summary>
public sealed record PersonSummaryInfo(
    Guid Id,
    string FirstName,
    string LastName,
    string? Nationality,
    DateOnly? BirthDate,
    string? Phone,
    string? Email,
    int CurrentDocumentCount)
{
    public string FullName => $"{FirstName} {LastName}";
}

/// <summary>
/// A person (driver) with the summary of its current documents (never the images). <c>IsAnonymized</c> persons keep
/// their id but have empty names and no data: display a "anonymized person" label instead of the name.
/// </summary>
public sealed record PersonInfo(
    Guid Id,
    string FirstName,
    string LastName,
    string? Nationality,
    DateOnly? BirthDate,
    string? Phone,
    string? Email,
    PostalAddressInfo? Address,
    bool IsAnonymized,
    Guid? IndividualCustomerId,
    IReadOnlyList<IdentityDocumentInfo> Documents)
{
    public string FullName => string.Join(' ', new[] { FirstName, LastName }.Where(part => part.Length > 0));
}

/// <summary>
/// A current identity document. <c>Number</c> is masked (last 4 characters) unless the current user holds
/// <c>customers.documents.read</c>; <c>HasFrontImage</c>/<c>HasBackImage</c> tell whether an image can be viewed through
/// the authorized (and logged) endpoint.
/// </summary>
public sealed record IdentityDocumentInfo(
    Guid Id,
    IdentityDocumentTypeInfo Type,
    string Number,
    string IssuingCountry,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    bool IsLatinScript,
    bool HasFrontImage,
    bool HasBackImage,
    DateTimeOffset? VerifiedAtUtc,
    string? VerifiedBy)
{
    public bool IsVerified => VerifiedAtUtc is not null;

    /// <summary>True when the document is still valid on <paramref name="date"/> (expiry date included).</summary>
    public bool IsValidUntil(DateOnly date) => ExpiresOn is null || ExpiresOn.Value >= date;
}

/// <summary>A postal address; <c>Country</c> is an ISO 3166-1 alpha-2 code.</summary>
public sealed record PostalAddressInfo(string Line1, string? Line2, string? PostalCode, string City, string Country);

/// <summary>
/// A payer. For an individual, <c>DisplayName</c> and <c>BillingAddress</c> come from the person; for a company, from
/// the company details (legal name, tax identifier "matricule fiscal", billing address).
/// </summary>
public sealed record CustomerInfo(
    Guid Id,
    CustomerKindInfo Kind,
    string DisplayName,
    Guid? PersonId,
    string? LegalName,
    string? TaxIdentifier,
    PostalAddressInfo? BillingAddress,
    string? Email,
    string? Phone);

/// <summary>Outcome of an eligibility check for given pick-up and return instants, with the thresholds applied.</summary>
public sealed record DriverEligibilityInfo(
    Guid PersonId,
    bool IsEligible,
    IReadOnlyList<EligibilityIssueInfo> Issues,
    int MinimumAge,
    int MinimumLicenseYears);

/// <summary>One reason why a driver is not eligible (may be shown to the applicant; the blacklist never is).</summary>
public sealed record EligibilityIssueInfo(
    EligibilityIssueKindInfo Kind,
    IdentityDocumentTypeInfo? DocumentType,
    DateOnly? Date,
    int? Required,
    int? Actual);

/// <summary>Identity document details entered by an agent (or later by the customer).</summary>
public sealed record IdentityDocumentInput(
    IdentityDocumentTypeInfo Type,
    string Number,
    string IssuingCountry,
    DateOnly? IssuedOn = null,
    DateOnly? ExpiresOn = null,
    bool IsLatinScript = true);

/// <summary>
/// A person to find again or create (quick creation by phone or at the counter, web funnel). Only the names are
/// required; phone, e-mail and document are the deduplication keys.
/// </summary>
public sealed record PersonRegistrationRequest(
    string FirstName,
    string LastName,
    string? Nationality = null,
    DateOnly? BirthDate = null,
    string? Phone = null,
    string? Email = null,
    PostalAddressInfo? Address = null,
    IdentityDocumentInput? Document = null);

/// <summary>
/// Result of <see cref="ICustomerRegistration.FindOrCreatePersonAsync"/>: the person used, how it was found,
/// whether the matched person has another name (same document typed with another name: check it), and the other
/// persons sharing the phone or e-mail (relatives, possible duplicates) for the agent to review.
/// </summary>
public sealed record PersonRegistrationResult(
    Guid PersonId,
    PersonMatchKindInfo Match,
    bool NameDiffers,
    IReadOnlyList<Guid> SimilarPersonIds)
{
    public bool Created => Match == PersonMatchKindInfo.Created;
}

/// <summary>A company payer to create.</summary>
public sealed record CompanyCustomerRequest(
    string LegalName,
    string TaxIdentifier,
    PostalAddressInfo BillingAddress,
    string? ContactName = null,
    string? Email = null,
    string? Phone = null);

/// <summary>How a registration found its person (ADR-0027).</summary>
public enum PersonMatchKindInfo
{
    /// <summary>No match: a new person was created.</summary>
    Created = 0,

    /// <summary>Same current document (type, issuing country, number).</summary>
    Document = 1,

    /// <summary>Same phone number and same name.</summary>
    Phone = 2,

    /// <summary>Same e-mail address and same name.</summary>
    Email = 3,
}

/// <summary>Type of identity document, as exposed to other modules (same values as the domain enum).</summary>
public enum IdentityDocumentTypeInfo
{
    NationalId = 1,
    Passport = 2,
    DrivingLicense = 3,
    InternationalDrivingPermit = 4,
}

/// <summary>Side of a document image (same values as the domain enum).</summary>
public enum DocumentImageSideInfo
{
    Front = 1,
    Back = 2,
}

/// <summary>Kind of payer (same values as the domain enum).</summary>
public enum CustomerKindInfo
{
    Individual = 1,
    Company = 2,
}

/// <summary>Purpose of a consent (same values as the domain enum).</summary>
public enum ConsentPurposeInfo
{
    MarketingEmail = 1,
    MarketingWhatsApp = 2,
    WhatsAppNotifications = 3,
    IdentityDocumentProcessing = 4,
}

/// <summary>Channel of a consent (same values as the domain enum).</summary>
public enum ConsentChannelInfo
{
    Web = 1,
    WhatsApp = 2,
    Phone = 3,
    Counter = 4,
    Email = 5,
}

/// <summary>Eligibility issue (same values as the domain enum).</summary>
public enum EligibilityIssueKindInfo
{
    BirthDateMissing = 1,
    UnderMinimumAge = 2,
    NationalityMissing = 3,
    IdentityDocumentMissing = 4,
    DrivingLicenseMissing = 5,
    LicenseHeldTooShort = 6,
    DocumentExpiresBeforeReturn = 7,
    InternationalPermitMissing = 8,
}
