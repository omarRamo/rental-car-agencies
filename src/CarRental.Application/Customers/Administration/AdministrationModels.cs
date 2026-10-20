using CarRental.Application.Customers.Contracts;

namespace CarRental.Application.Customers.Administration;

/// <summary>A row of the persons list of the back office.</summary>
public sealed record PersonListItem(
    Guid Id,
    string FirstName,
    string LastName,
    string? Nationality,
    DateOnly? BirthDate,
    string? Phone,
    string? Email,
    int CurrentDocumentCount,
    bool IsBlacklisted)
{
    public string FullName => $"{FirstName} {LastName}";
}

/// <summary>The person page of the back office: identity, documents, consents, blacklist, and what the user may do.</summary>
public sealed record PersonFile(
    Guid Id,
    string FirstName,
    string LastName,
    string? Nationality,
    DateOnly? BirthDate,
    string? Phone,
    string? Email,
    PostalAddressInfo? Address,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? AnonymizedAtUtc,
    Guid? IndividualCustomerId,
    IReadOnlyList<DocumentFileItem> Documents,
    IReadOnlyList<ConsentItem> Consents,
    IReadOnlyList<BlacklistItem> Blacklist,
    PersonFilePermissions Permissions)
{
    public bool IsAnonymized => AnonymizedAtUtc is not null;

    public bool IsBlacklisted => Blacklist.Any(entry => entry.IsActive);

    public string FullName => string.Join(' ', new[] { FirstName, LastName }.Where(part => part.Length > 0));

    public IEnumerable<DocumentFileItem> CurrentDocuments => Documents.Where(document => document.IsCurrent);

    public IEnumerable<DocumentFileItem> SupersededDocuments => Documents.Where(document => !document.IsCurrent);
}

/// <summary>What the current user may do on the person page (the services check again).</summary>
public sealed record PersonFilePermissions(bool CanManage, bool CanViewDocumentImages, bool CanManagePrivacy);

/// <summary>A document on the person page. <c>Number</c> is masked without <c>customers.documents.read</c>.</summary>
public sealed record DocumentFileItem(
    Guid Id,
    IdentityDocumentTypeInfo Type,
    string Number,
    string IssuingCountry,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    bool IsLatinScript,
    DocumentImageInfo? Front,
    DocumentImageInfo? Back,
    DateTimeOffset AddedAtUtc,
    DateTimeOffset? VerifiedAtUtc,
    string? VerifiedBy,
    DateTimeOffset? SupersededAtUtc)
{
    public bool IsCurrent => SupersededAtUtc is null;

    public bool IsVerified => VerifiedAtUtc is not null;

    public DocumentImageInfo? ImageOf(DocumentImageSideInfo side) => side == DocumentImageSideInfo.Front ? Front : Back;

    public bool IsValidUntil(DateOnly date) => ExpiresOn is null || ExpiresOn.Value >= date;
}

/// <summary>Metadata of a stored image (never its path nor its bytes).</summary>
public sealed record DocumentImageInfo(string ContentType, long Length, DateTimeOffset UploadedAtUtc)
{
    public bool IsPdf => string.Equals(ContentType, "application/pdf", StringComparison.Ordinal);
}

public sealed record ConsentItem(
    Guid Id,
    ConsentPurposeInfo Purpose,
    ConsentChannelInfo GivenThrough,
    DateTimeOffset GivenAtUtc,
    ConsentChannelInfo? WithdrawnThrough,
    DateTimeOffset? WithdrawnAtUtc)
{
    public bool IsActive => WithdrawnAtUtc is null;
}

public sealed record BlacklistItem(
    Guid Id,
    string Reason,
    DateTimeOffset AddedAtUtc,
    string? AddedBy,
    DateTimeOffset? LiftedAtUtc,
    string? LiftedBy,
    string? LiftReason)
{
    public bool IsActive => LiftedAtUtc is null;
}

/// <summary>Editable details of a person (the documents have their own forms).</summary>
public sealed record PersonDetailsForm(
    string FirstName,
    string LastName,
    string? Nationality = null,
    DateOnly? BirthDate = null,
    string? Phone = null,
    string? Email = null,
    PostalAddressInfo? Address = null);

/// <summary>A row of the companies list of the back office.</summary>
public sealed record CompanyListItem(Guid Id, string LegalName, string TaxIdentifier, string City, string? Email, string? Phone);

/// <summary>A company payer, for its edit form.</summary>
public sealed record CompanyFile(
    Guid Id,
    string LegalName,
    string TaxIdentifier,
    PostalAddressInfo BillingAddress,
    string? ContactName,
    string? Email,
    string? Phone,
    DateTimeOffset CreatedAtUtc);
