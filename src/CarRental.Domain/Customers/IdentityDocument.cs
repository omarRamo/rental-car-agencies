using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>
/// An identity document of a person: national identity card (CIN), passport, driving licence or international driving
/// permit, with its number, issuing country, dates, latin-script flag (licences) and the references of its front and
/// back images. Part of the <see cref="Person"/> aggregate: it is changed through the person only.
/// </summary>
/// <remarks>A document still valid at the return date is reused from one rental to the next (SPEC §6).</remarks>
public sealed class IdentityDocument : Entity, IAgencyOwned
{
    public const int VerifierNameMaxLength = AuditActor.UserNameMaxLength;

    private IdentityDocument()
    {
    }

    private IdentityDocument(Guid id, Guid personId, Guid agencyId, IdentityDocumentFacts facts, DateTimeOffset now)
        : base(id)
    {
        PersonId = personId;
        AgencyId = agencyId;
        Type = facts.Type;
        AddedAtUtc = now;
        Apply(facts);
    }

    public Guid PersonId { get; private set; }

    /// <summary>The agency of the person (given by the person at creation).</summary>
    public Guid AgencyId { get; private set; }

    public IdentityDocumentType Type { get; private set; }

    /// <summary>Number as displayed (upper case), e.g. <c>12 AB-34567</c>.</summary>
    public string Number { get; private set; } = null!;

    /// <summary>Search and deduplication key: ASCII letters and digits of <see cref="Number"/>.</summary>
    public string NumberKey { get; private set; } = null!;

    /// <summary>ISO 3166-1 alpha-2 code of the issuing country.</summary>
    public string IssuingCountry { get; private set; } = null!;

    public DateOnly? IssuedOn { get; private set; }

    /// <summary>Last day of validity (included); <see langword="null"/> when the document does not expire (old CIN).</summary>
    public DateOnly? ExpiresOn { get; private set; }

    /// <summary>For a driving licence: written in latin script (otherwise an international permit is required). Always true for other types.</summary>
    public bool IsLatinScript { get; private set; }

    public DocumentImage? FrontImage { get; private set; }

    public DocumentImage? BackImage { get; private set; }

    public DateTimeOffset AddedAtUtc { get; private set; }

    public DateTimeOffset? VerifiedAtUtc { get; private set; }

    public Guid? VerifiedByUserId { get; private set; }

    public string? VerifiedByName { get; private set; }

    /// <summary>When a newer document of the same type replaced this one; a superseded document is kept as history.</summary>
    public DateTimeOffset? SupersededAtUtc { get; private set; }

    public bool IsCurrent => SupersededAtUtc is null;

    public bool IsVerified => VerifiedAtUtc is not null;

    /// <summary>The front and back images that exist (a method, so that the persistence never maps it).</summary>
    public IReadOnlyList<DocumentImage> ImageFiles() =>
        new[] { FrontImage, BackImage }.Where(image => image is not null).Select(image => image!).ToArray();

    /// <summary>True when the document is still valid on <paramref name="date"/> (expiry date included).</summary>
    public bool IsValidUntil(DateOnly date) => ExpiresOn is null || ExpiresOn.Value >= date;

    public DocumentImage? ImageOf(DocumentImageSide side) => side switch
    {
        DocumentImageSide.Front => FrontImage,
        DocumentImageSide.Back => BackImage,
        _ => null,
    };

    internal static IdentityDocument Create(Guid personId, Guid agencyId, IdentityDocumentFacts facts, DateTimeOffset now) =>
        new(NewId(), personId, agencyId, facts, now);

    internal bool HasSameNumber(IdentityDocumentFacts facts) =>
        string.Equals(NumberKey, facts.NumberKey, StringComparison.Ordinal)
        && string.Equals(IssuingCountry, facts.IssuingCountry, StringComparison.Ordinal);

    internal void Correct(IdentityDocumentFacts facts)
    {
        Apply(facts);
        ResetVerification();
    }

    internal void Supersede(DateTimeOffset now) => SupersededAtUtc = now;

    /// <summary>Sets the image of <paramref name="side"/> and returns the previous one (the evidence changed: verification is reset).</summary>
    internal DocumentImage? ReplaceImage(DocumentImageSide side, DocumentImage? image)
    {
        var previous = ImageOf(side);
        if (side == DocumentImageSide.Front)
        {
            FrontImage = image;
        }
        else
        {
            BackImage = image;
        }

        ResetVerification();
        return previous;
    }

    /// <summary>Removes the images whose retention reference is before <paramref name="cutoff"/>; returns them.</summary>
    internal IReadOnlyList<DocumentImage> RemoveImagesOlderThan(DateTimeOffset cutoff, DateTimeOffset? lastRentalClosedAtUtc)
    {
        var removed = new List<DocumentImage>(2);
        foreach (var side in new[] { DocumentImageSide.Front, DocumentImageSide.Back })
        {
            if (ImageOf(side) is { } image && Later(image.UploadedAtUtc, lastRentalClosedAtUtc) < cutoff)
            {
                removed.Add(image);
                if (side == DocumentImageSide.Front)
                {
                    FrontImage = null;
                }
                else
                {
                    BackImage = null;
                }
            }
        }

        return removed;
    }

    internal void Verify(AuditActor verifier, DateTimeOffset now)
    {
        VerifiedAtUtc = now;
        VerifiedByUserId = verifier.UserId;
        VerifiedByName = verifier.UserName is { Length: > VerifierNameMaxLength } name ? name[..VerifierNameMaxLength] : verifier.UserName;
    }

    private static DateTimeOffset Later(DateTimeOffset value, DateTimeOffset? other) =>
        other is { } instant && instant > value ? instant : value;

    private void ResetVerification()
    {
        VerifiedAtUtc = null;
        VerifiedByUserId = null;
        VerifiedByName = null;
    }

    private void Apply(IdentityDocumentFacts facts)
    {
        Number = facts.Number;
        NumberKey = facts.NumberKey;
        IssuingCountry = facts.IssuingCountry;
        IssuedOn = facts.IssuedOn;
        ExpiresOn = facts.ExpiresOn;
        IsLatinScript = facts.IsLatinScript;
    }
}

/// <summary>What the agent (or the customer) enters for a document.</summary>
public sealed record IdentityDocumentDetails(
    IdentityDocumentType Type,
    string Number,
    string IssuingCountry,
    DateOnly? IssuedOn = null,
    DateOnly? ExpiresOn = null,
    bool IsLatinScript = true);

/// <summary>Validated and normalized document details.</summary>
internal sealed record IdentityDocumentFacts(
    IdentityDocumentType Type,
    string Number,
    string NumberKey,
    string IssuingCountry,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    bool IsLatinScript)
{
    private static readonly DateOnly OldestIssueDate = new(1900, 1, 1);

    public static Result<IdentityDocumentFacts> From(IdentityDocumentDetails details, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (!Enum.IsDefined(details.Type))
        {
            return CustomersErrors.DocumentTypeInvalid;
        }

        var country = CountryCodes.Normalize(details.IssuingCountry);
        if (country.IsFailure)
        {
            return country.Error;
        }

        var number = DocumentNumbers.Clean(details.Number);
        var key = DocumentNumbers.KeyOf(number);
        if (number.Length is 0 or > DocumentNumbers.NumberMaxLength
            || !DocumentNumbers.HasOnlyAllowedCharacters(number)
            || key.Length is < DocumentNumbers.KeyMinLength or > DocumentNumbers.KeyMaxLength)
        {
            return CustomersErrors.DocumentNumberInvalid;
        }

        if (details.Type == IdentityDocumentType.NationalId
            && country.Value == CountryCodes.Tunisia
            && (key.Length != DocumentNumbers.TunisianNationalIdLength || !key.All(char.IsAsciiDigit)))
        {
            return CustomersErrors.NationalIdNumberInvalid;
        }

        if (details.Type == IdentityDocumentType.DrivingLicense && details.IssuedOn is null)
        {
            return CustomersErrors.DocumentIssueDateRequired;
        }

        if ((details.Type is IdentityDocumentType.Passport or IdentityDocumentType.InternationalDrivingPermit) && details.ExpiresOn is null)
        {
            return CustomersErrors.DocumentExpiryDateRequired;
        }

        if (details.IssuedOn is { } issued
            && (issued > today || issued < OldestIssueDate || (details.ExpiresOn is { } expires && expires < issued)))
        {
            return CustomersErrors.DocumentDatesInvalid;
        }

        return new IdentityDocumentFacts(
            details.Type,
            number,
            key,
            country.Value,
            details.IssuedOn,
            details.ExpiresOn,
            details.Type != IdentityDocumentType.DrivingLicense || details.IsLatinScript);
    }
}
