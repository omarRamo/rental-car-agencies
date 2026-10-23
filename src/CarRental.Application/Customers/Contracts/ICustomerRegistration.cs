using CarRental.Domain.Common;

namespace CarRental.Application.Customers.Contracts;

/// <summary>
/// Write API of the Customers module for other modules: quick creation of a booking (phone, counter), and later the
/// web funnel and "compléter mon dossier". Every method requires <c>customers.manage</c> (the system actor has it),
/// except <see cref="RecordRentalClosedAsync"/>, a technical signal of the Bookings module.
/// </summary>
/// <remarks>
/// Customers belong to an agency (ADR-0067): a new person, payer or staged image is registered for the agency the
/// request names (<c>AgencyId</c>), else for the agency of the operation (<c>customers.agency_required</c> without
/// either); persons are only ever found again among the customers of that agency.
/// </remarks>
public interface ICustomerRegistration
{
    /// <summary>
    /// Finds the person again — same current document (type, country, number) first, then same phone or same e-mail
    /// with the same name, among the persons of the agency — or creates it (ADR-0027). A found person only gets its
    /// missing details filled in (never overwritten) and the document added when it had none of that type.
    /// </summary>
    Task<Result<PersonRegistrationResult>> FindOrCreatePersonAsync(PersonRegistrationRequest request, CancellationToken cancellationToken = default);

    /// <summary>The individual payer of a person (in the agency of the person), created on first use. Returns the customer id.</summary>
    Task<Result<Guid>> GetOrCreateIndividualCustomerAsync(Guid personId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a document to a person (a new number supersedes the current document of the same type, the same number
    /// corrects it), with optional front and back images (JPEG, PNG or PDF, 10 MB max, checked on their content).
    /// Returns the document id.
    /// </summary>
    Task<Result<Guid>> AddOrReplaceDocumentAsync(
        Guid personId,
        IdentityDocumentInput document,
        Stream? frontImage = null,
        Stream? backImage = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stores (or replaces) the image of one side of a current document. The previous file is deleted after the commit.</summary>
    Task<Result> UploadDocumentImageAsync(Guid documentId, DocumentImageSideInfo side, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Creates a company payer; a company with the same tax identifier is a conflict. Returns the customer id.</summary>
    Task<Result<Guid>> CreateCompanyCustomerAsync(CompanyCustomerRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gives (<paramref name="given"/>) or withdraws a consent, through <paramref name="channel"/>.</summary>
    Task<Result> SetConsentAsync(
        Guid personId,
        ConsentPurposeInfo purpose,
        ConsentChannelInfo channel,
        bool given,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A rental of the person was closed at <paramref name="closedAtUtc"/>: the retention period of its document images
    /// restarts from there. Called by Bookings when a rental is closed; no permission required.
    /// </summary>
    Task<Result> RecordRentalClosedAsync(Guid personId, DateTimeOffset closedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Public pages (ADR-0050): keeps aside an image uploaded by a visitor before its submission — real format (JPEG,
    /// PNG, PDF) checked on the content, 10 MB at most, stored privately under an identifier — for the agency
    /// <paramref name="agencyId"/> (the agency of the operation when <see langword="null"/>). It is used once by
    /// <see cref="RegisterSubmittedDriverAsync"/> or <see cref="SubmitDocumentImageAsync"/> for that agency, otherwise
    /// deleted after a day.
    /// </summary>
    Task<Result<StagedDocumentImageInfo>> StageDocumentImageAsync(Stream content, Guid? agencyId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Public request (ADR-0050): registers a driver submitted by a visitor. When nobody matches (same rules as
    /// <see cref="FindOrCreatePersonAsync"/>, every submitted document considered), the person is created with its
    /// documents, images and consents. When a person is already known, it is <b>never changed</b>: the submission
    /// (details, documents, images, consents) waits for an agent, who reconciles it at the validation. The result is the
    /// same for the visitor in both cases.
    /// </summary>
    Task<Result<SubmittedDriverResult>> RegisterSubmittedDriverAsync(SubmittedDriverRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// "Ma réservation": adds the image of one side of a document to the pending submission of the driver for the booking
    /// (created when there is none). Applied to the person only when an agent accepts it.
    /// </summary>
    Task<Result> SubmitDocumentImageAsync(DocumentImageSubmission submission, CancellationToken cancellationToken = default);

    /// <summary>The request ended (refused, expired, cancelled): its pending submissions are rejected and their images deleted.</summary>
    Task<Result> DiscardDriverSubmissionsAsync(Guid bookingId, CancellationToken cancellationToken = default);
}
