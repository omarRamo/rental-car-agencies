using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>How a public submission found a person already known (ADR-0050); never shown to the visitor.</summary>
public enum SubmissionMatch
{
    /// <summary>No person matched: the submission created its person.</summary>
    None = 0,

    /// <summary>Same current document (type, issuing country, number).</summary>
    Document = 1,

    /// <summary>Same phone number and same name.</summary>
    Phone = 2,

    /// <summary>Same e-mail address and same name.</summary>
    Email = 3,

    /// <summary>A driver of the booking already ("Ma réservation"): images sent for its file.</summary>
    Booking = 4,
}

/// <summary>Where a driver submission stands.</summary>
public enum DriverSubmissionState
{
    /// <summary>The submission created its person: its documents were applied at once (nothing to protect).</summary>
    Applied = 1,

    /// <summary>For a person already known: waits for an agent, who accepts or rejects it.</summary>
    Pending = 2,

    /// <summary>An agent applied it to the person (documents added, missing details completed).</summary>
    Accepted = 3,

    /// <summary>An agent (or the end of the request) discarded it; its images were deleted.</summary>
    Rejected = 4,
}

/// <summary>
/// What a visitor submitted for a driver of a booking through a public page (booking funnel, "Ma réservation"):
/// the details as typed, the documents and their images, and the consents (ADR-0050). A public submission never
/// changes a person already known: for such a person it stays <see cref="DriverSubmissionState.Pending"/> until an
/// agent reconciles it; the submission that created its person is <see cref="DriverSubmissionState.Applied"/> and only
/// records what was sent for the booking. The details also give the contact of the booking (the e-mail typed by the
/// visitor, whatever the stored person holds).
/// </summary>
public sealed class DriverSubmission : AggregateRoot
{
    private readonly List<SubmittedDocument> _documents = [];

    private DriverSubmission()
    {
    }

    private DriverSubmission(
        Guid personId,
        Guid bookingId,
        ConsentChannel channel,
        DriverSubmissionState state,
        SubmissionMatch match,
        bool nameDiffers,
        PersonFacts? details,
        SubmittedConsents consents,
        DateTimeOffset now)
        : base(NewId())
    {
        PersonId = personId;
        BookingId = bookingId;
        Channel = channel;
        State = state;
        Match = match;
        NameDiffers = nameDiffers;
        FirstName = details?.FirstName;
        LastName = details?.LastName;
        Nationality = details?.Nationality;
        BirthDate = details?.BirthDate;
        Phone = details?.Phone;
        Email = details?.Email;
        DocumentProcessingConsent = consents.DocumentProcessing;
        WhatsAppNotificationsConsent = consents.WhatsAppNotifications;
        MarketingConsent = consents.Marketing;
        SubmittedAtUtc = now.ToUniversalTime();
        LastModifiedAtUtc = SubmittedAtUtc;
    }

    /// <summary>The person the submission is for (found again, or created by it).</summary>
    public Guid PersonId { get; private set; }

    /// <summary>The booking it was sent for (id only: the Customers module does not read bookings).</summary>
    public Guid BookingId { get; private set; }

    public ConsentChannel Channel { get; private set; }

    public DriverSubmissionState State { get; private set; }

    public SubmissionMatch Match { get; private set; }

    /// <summary>The person was found by its document while the name typed is another one: to check.</summary>
    public bool NameDiffers { get; private set; }

    /// <summary>Details as typed; <see langword="null"/> for a submission of images only ("Ma réservation").</summary>
    public string? FirstName { get; private set; }

    public string? LastName { get; private set; }

    public string? Nationality { get; private set; }

    public DateOnly? BirthDate { get; private set; }

    /// <summary>E.164.</summary>
    public string? Phone { get; private set; }

    /// <summary>Lower case: the contact of the booking for the notifications and "Ma réservation".</summary>
    public string? Email { get; private set; }

    public bool DocumentProcessingConsent { get; private set; }

    public bool WhatsAppNotificationsConsent { get; private set; }

    public bool MarketingConsent { get; private set; }

    public DateTimeOffset SubmittedAtUtc { get; private set; }

    public DateTimeOffset LastModifiedAtUtc { get; private set; }

    public DateTimeOffset? ReviewedAtUtc { get; private set; }

    public Guid? ReviewedByUserId { get; private set; }

    public string? ReviewedByName { get; private set; }

    public IReadOnlyList<SubmittedDocument> Documents => _documents;

    public bool IsPending => State == DriverSubmissionState.Pending;

    /// <summary>The details as typed, when the submission has some.</summary>
    public PersonDetails? Details => FirstName is null || LastName is null
        ? null
        : new PersonDetails(FirstName, LastName, Nationality, BirthDate, Phone, Email);

    /// <summary>
    /// A submission: <see cref="DriverSubmissionState.Applied"/> when <paramref name="match"/> is
    /// <see cref="SubmissionMatch.None"/> (its person was just created from it), otherwise pending for an agent.
    /// <paramref name="details"/> may be omitted for images only; at least one document or the details are required,
    /// and the consent to the processing of the documents whenever a document is sent.
    /// </summary>
    public static Result<DriverSubmission> Create(
        Guid personId,
        Guid bookingId,
        ConsentChannel channel,
        PersonDetails? details,
        SubmissionMatch match,
        bool nameDiffers,
        SubmittedConsents consents,
        IReadOnlyList<SubmittedDocumentEntry> documents,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(consents);
        ArgumentNullException.ThrowIfNull(documents);
        if (personId == Guid.Empty || bookingId == Guid.Empty)
        {
            throw new ArgumentException("A submission is for a person and a booking.");
        }

        if (!Enum.IsDefined(channel))
        {
            return CustomersErrors.ConsentChannelInvalid;
        }

        PersonFacts? facts = null;
        if (details is not null)
        {
            var validated = PersonFacts.From(details, TunisTime.DateOf(now));
            if (validated.IsFailure)
            {
                return validated.Error;
            }

            facts = validated.Value;
        }

        if (facts is null && documents.Count == 0)
        {
            return CustomersErrors.SubmissionEmpty;
        }

        if (documents.Count > 0 && !consents.DocumentProcessing)
        {
            return CustomersErrors.SubmissionConsentRequired;
        }

        var state = match == SubmissionMatch.None ? DriverSubmissionState.Applied : DriverSubmissionState.Pending;
        var submission = new DriverSubmission(personId, bookingId, channel, state, match, nameDiffers, facts, consents, now);
        foreach (var entry in documents)
        {
            var added = submission.Add(entry, now);
            if (added.IsFailure)
            {
                return added.Error;
            }

            if (state == DriverSubmissionState.Applied)
            {
                // The images belong to the documents of the new person: only their presence is recorded here.
                submission._documents[^1].HandOverImages();
            }
        }

        submission.Raise(new DriverSubmissionReceived(submission.Id, personId, bookingId, submission.IsPending));
        return submission;
    }

    /// <summary>
    /// Adds the image of one side of a document to a pending submission ("Ma réservation"): to the entry of that type
    /// without details when there is one, else a new entry. A previous image of the same side is discarded.
    /// </summary>
    public Result AddImage(IdentityDocumentType type, DocumentImageSide side, DocumentImage image, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!IsPending)
        {
            return CustomersErrors.SubmissionNotPending;
        }

        if (!Enum.IsDefined(type))
        {
            return CustomersErrors.DocumentTypeInvalid;
        }

        if (!Enum.IsDefined(side))
        {
            return CustomersErrors.ImageSideInvalid;
        }

        var document = _documents.LastOrDefault(d => d.Type == type);
        if (document is null)
        {
            document = SubmittedDocument.ImagesOnly(Id, type);
            _documents.Add(document);
        }

        if (document.ReplaceImage(side, image) is { } previous && !string.Equals(previous.Path, image.Path, StringComparison.Ordinal))
        {
            Raise(new DocumentImagesDiscarded(PersonId, document.Id, [previous.Path]));
        }

        DocumentProcessingConsent = true;
        LastModifiedAtUtc = now.ToUniversalTime();
        return Result.Success();
    }

    /// <summary>
    /// An agent applied the submission to the person (the application added the documents and moved their images):
    /// the image references are handed over, only their presence is kept here.
    /// </summary>
    public Result Accept(AuditActor reviewer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reviewer);
        if (!IsPending)
        {
            return CustomersErrors.SubmissionNotPending;
        }

        foreach (var document in _documents)
        {
            document.HandOverImages();
        }

        Review(DriverSubmissionState.Accepted, reviewer, now);
        return Result.Success();
    }

    /// <summary>Discards a pending submission (agent decision, refused or expired request): its images are deleted after the commit.</summary>
    public Result Reject(AuditActor reviewer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reviewer);
        if (!IsPending)
        {
            return CustomersErrors.SubmissionNotPending;
        }

        DiscardImages();
        Review(DriverSubmissionState.Rejected, reviewer, now);
        return Result.Success();
    }

    /// <summary>Data subject right (anonymization of the person): the images of a pending submission are discarded before it is deleted.</summary>
    public void DiscardForErasure() => DiscardImages();

    private Result Add(SubmittedDocumentEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Details is null)
        {
            if (entry.FrontImage is null && entry.BackImage is null)
            {
                return CustomersErrors.SubmissionEmpty;
            }

            var imagesOnly = SubmittedDocument.ImagesOnly(Id, entry.Type);
            imagesOnly.ReplaceImage(DocumentImageSide.Front, entry.FrontImage);
            imagesOnly.ReplaceImage(DocumentImageSide.Back, entry.BackImage);
            _documents.Add(imagesOnly);
            return Result.Success();
        }

        var facts = IdentityDocumentFacts.From(entry.Details, TunisTime.DateOf(now));
        if (facts.IsFailure)
        {
            return facts.Error;
        }

        if (facts.Value.Type != entry.Type)
        {
            return CustomersErrors.DocumentTypeInvalid;
        }

        var document = SubmittedDocument.WithDetails(Id, facts.Value);
        document.ReplaceImage(DocumentImageSide.Front, entry.FrontImage);
        document.ReplaceImage(DocumentImageSide.Back, entry.BackImage);
        _documents.Add(document);
        return Result.Success();
    }

    private void DiscardImages()
    {
        foreach (var document in _documents)
        {
            var paths = document.ImageFiles().Select(image => image.Path).ToArray();
            document.HandOverImages();
            if (paths.Length > 0)
            {
                Raise(new DocumentImagesDiscarded(PersonId, document.Id, paths));
            }
        }
    }

    private void Review(DriverSubmissionState state, AuditActor reviewer, DateTimeOffset now)
    {
        State = state;
        ReviewedAtUtc = now.ToUniversalTime();
        ReviewedByUserId = reviewer.UserId;
        ReviewedByName = reviewer.UserName is { Length: > IdentityDocument.VerifierNameMaxLength } name
            ? name[..IdentityDocument.VerifierNameMaxLength]
            : reviewer.UserName;
        LastModifiedAtUtc = ReviewedAtUtc.Value;
        Raise(new DriverSubmissionReviewed(Id, PersonId, BookingId, state == DriverSubmissionState.Accepted));
    }
}

/// <summary>
/// A document of a <see cref="DriverSubmission"/>: its details as typed (validated like a document of a person) and
/// the images of its sides, or images only (the details are on file). After the review only the presence of the
/// images is kept (the files belong to the person, or were deleted).
/// </summary>
public sealed class SubmittedDocument : Entity
{
    private SubmittedDocument()
    {
    }

    private SubmittedDocument(Guid submissionId, IdentityDocumentType type)
        : base(NewId())
    {
        SubmissionId = submissionId;
        Type = type;
        IsLatinScript = true;
    }

    public Guid SubmissionId { get; private set; }

    public IdentityDocumentType Type { get; private set; }

    /// <summary>Number as typed (upper case); <see langword="null"/> for images only.</summary>
    public string? Number { get; private set; }

    public string? NumberKey { get; private set; }

    public string? IssuingCountry { get; private set; }

    public DateOnly? IssuedOn { get; private set; }

    public DateOnly? ExpiresOn { get; private set; }

    public bool IsLatinScript { get; private set; }

    public DocumentImage? FrontImage { get; private set; }

    public DocumentImage? BackImage { get; private set; }

    /// <summary>A front image was sent (kept after the review, when the file belongs to the person or was deleted).</summary>
    public bool HasFrontImage { get; private set; }

    public bool HasBackImage { get; private set; }

    public bool HasDetails => Number is not null;

    /// <summary>The details of the document, for the agent who applies it; <see langword="null"/> for images only.</summary>
    public IdentityDocumentDetails? Details => Number is null || IssuingCountry is null
        ? null
        : new IdentityDocumentDetails(Type, Number, IssuingCountry, IssuedOn, ExpiresOn, IsLatinScript);

    public IReadOnlyList<DocumentImage> ImageFiles() =>
        new[] { FrontImage, BackImage }.Where(image => image is not null).Select(image => image!).ToArray();

    public DocumentImage? ImageOf(DocumentImageSide side) => side == DocumentImageSide.Back ? BackImage : FrontImage;

    internal static SubmittedDocument ImagesOnly(Guid submissionId, IdentityDocumentType type) => new(submissionId, type);

    internal static SubmittedDocument WithDetails(Guid submissionId, IdentityDocumentFacts facts)
    {
        var document = new SubmittedDocument(submissionId, facts.Type)
        {
            Number = facts.Number,
            NumberKey = facts.NumberKey,
            IssuingCountry = facts.IssuingCountry,
            IssuedOn = facts.IssuedOn,
            ExpiresOn = facts.ExpiresOn,
            IsLatinScript = facts.IsLatinScript,
        };
        return document;
    }

    /// <summary>Sets the image of a side (none: unchanged) and returns the previous one.</summary>
    internal DocumentImage? ReplaceImage(DocumentImageSide side, DocumentImage? image)
    {
        if (image is null)
        {
            return null;
        }

        var previous = ImageOf(side);
        if (side == DocumentImageSide.Back)
        {
            BackImage = image;
            HasBackImage = true;
        }
        else
        {
            FrontImage = image;
            HasFrontImage = true;
        }

        return previous;
    }

    /// <summary>Forgets the file references (moved to the person, or deleted); the presence flags stay.</summary>
    internal void HandOverImages()
    {
        FrontImage = null;
        BackImage = null;
    }
}

/// <summary>The consents given with a submission (applied to a new person at once, to a known person on acceptance).</summary>
public sealed record SubmittedConsents(bool DocumentProcessing, bool WhatsAppNotifications, bool Marketing)
{
    public static SubmittedConsents ImagesOnly { get; } = new(true, false, false);
}

/// <summary>A document to submit: its type, its details (none for images only) and the stored images of its sides.</summary>
public sealed record SubmittedDocumentEntry(
    IdentityDocumentType Type,
    IdentityDocumentDetails? Details,
    DocumentImage? FrontImage = null,
    DocumentImage? BackImage = null);
