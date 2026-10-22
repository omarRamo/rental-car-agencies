namespace CarRental.Application.Customers.Contracts;

/// <summary>
/// What a visitor typed for a driver in a public page (booking funnel): identity, contact and documents (identity
/// document, driving licence, international permit). Checked without reading any stored person
/// (<see cref="ICustomersQueries.CheckSubmittedDriverAsync"/>), then registered by
/// <see cref="ICustomerRegistration.RegisterSubmittedDriverAsync"/> (ADR-0050).
/// </summary>
public sealed record SubmittedDriverDetails(
    string FirstName,
    string LastName,
    string? Nationality,
    DateOnly? BirthDate,
    string? Phone,
    string? Email,
    IReadOnlyList<IdentityDocumentInput> Documents);

/// <summary>A problem of a submitted driver: the document concerned (<see langword="null"/> for the person details) and the error code.</summary>
public sealed record SubmittedDriverError(IdentityDocumentTypeInfo? DocumentType, string Code);

/// <summary>
/// The check of a submitted driver: validation errors of the details and of each document (all of them, not only the
/// first), and the eligibility for the rental when everything is valid. Nothing stored is read nor revealed.
/// </summary>
public sealed record SubmittedDriverCheck(IReadOnlyList<SubmittedDriverError> Errors, DriverEligibilityInfo? Eligibility)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>The images uploaded for one document of a submitted driver (ids from <see cref="ICustomerRegistration.StageDocumentImageAsync"/>).</summary>
public sealed record SubmittedDocumentImages(IdentityDocumentTypeInfo Type, Guid? FrontStagedImageId, Guid? BackStagedImageId);

/// <summary>
/// A driver submitted with a public request: the booking it is for (id only), the channel, the details, the images and
/// the consents (document processing mandatory when documents are sent; WhatsApp notifications and marketing optional
/// and separate).
/// </summary>
public sealed record SubmittedDriverRequest(
    Guid BookingId,
    ConsentChannelInfo Channel,
    SubmittedDriverDetails Driver,
    IReadOnlyList<SubmittedDocumentImages> Images,
    bool DocumentProcessingConsent,
    bool WhatsAppNotificationsConsent = false,
    bool MarketingConsent = false)
{
    /// <summary>
    /// The agency of the booking (the agency of the public site); <see langword="null"/> for the agency of the
    /// operation. The driver is found again among the customers of that agency only, and its staged images too.
    /// </summary>
    public Guid? AgencyId { get; init; }
}

/// <summary>
/// The person a submitted driver was registered as. <see cref="RequiresReview"/> is internal information for the back
/// office (a person already known, left unchanged): never show it, nor anything derived from it, to the visitor.
/// </summary>
public sealed record SubmittedDriverResult(Guid PersonId, Guid SubmissionId, bool RequiresReview);

/// <summary>An image uploaded by a visitor and kept aside until its submission (real format checked, 10 MB at most).</summary>
public sealed record StagedDocumentImageInfo(Guid Id, string ContentType, long Length);

/// <summary>The image of one side of a document sent from "Ma réservation" for a driver of a booking.</summary>
public sealed record DocumentImageSubmission(
    Guid PersonId,
    Guid BookingId,
    ConsentChannelInfo Channel,
    IdentityDocumentTypeInfo Type,
    DocumentImageSideInfo Side,
    Guid StagedImageId);

/// <summary>Where a driver submission stands (same values as the domain enum).</summary>
public enum DriverSubmissionStateInfo
{
    Applied = 1,
    Pending = 2,
    Accepted = 3,
    Rejected = 4,
}

/// <summary>A document of a submission: its type, whether details were typed, and the sides sent.</summary>
public sealed record SubmittedDocumentInfo(Guid Id, IdentityDocumentTypeInfo Type, bool HasDetails, bool HasFrontImage, bool HasBackImage);

/// <summary>A driver submission for a booking: what the visitor sent (never the stored data of the person).</summary>
public sealed record DriverSubmissionInfo(
    Guid Id,
    Guid PersonId,
    Guid BookingId,
    DriverSubmissionStateInfo State,
    DateTimeOffset SubmittedAtUtc,
    IReadOnlyList<SubmittedDocumentInfo> Documents)
{
    public bool IsPending => State == DriverSubmissionStateInfo.Pending;
}

/// <summary>
/// Who to write to about a booking: the names and contact typed with its public submission when there is one,
/// otherwise those of the person (bookings made by an agent).
/// </summary>
public sealed record BookingContactInfo(string FirstName, string LastName, string? Email, string? Phone)
{
    public string FullName => string.Join(' ', new[] { FirstName, LastName }.Where(part => part.Length > 0));
}
