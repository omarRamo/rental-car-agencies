using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>
/// A natural person who drives (main or secondary driver) and may pay: identity, contact details, identity documents,
/// consents and blacklist history. Found again without duplicates by phone, document number or e-mail (ADR-0027).
/// </summary>
/// <remarks>
/// <para>Every change goes through a method of the aggregate and updates <see cref="LastModifiedAtUtc"/>, so that the
/// row version of the person also protects its documents, consents and blacklist entries against concurrent edits.</para>
/// <para>Anonymization keeps the identifier (referenced by bookings and invoices) and scrubs everything else.</para>
/// </remarks>
public sealed class Person : AggregateRoot
{
    public const int NameMaxLength = 100;

    /// <summary>Normalized "first last" name used by the search.</summary>
    public const int SearchNameMaxLength = (2 * NameMaxLength) + 1;

    private readonly List<IdentityDocument> _documents = [];
    private readonly List<Consent> _consents = [];
    private readonly List<BlacklistEntry> _blacklistEntries = [];

    private Person()
    {
    }

    private Person(Guid id, PersonFacts facts, DateTimeOffset now)
        : base(id)
    {
        Apply(facts);
        CreatedAtUtc = now;
        LastModifiedAtUtc = now;
    }

    public string FirstName { get; private set; } = null!;

    public string LastName { get; private set; } = null!;

    /// <summary>Lower case, without accents nor punctuation (see <see cref="Customers.SearchText"/>).</summary>
    public string SearchName { get; private set; } = null!;

    /// <summary>ISO 3166-1 alpha-2 code; decides the identity document required (CIN for TN, passport otherwise).</summary>
    public string? Nationality { get; private set; }

    public DateOnly? BirthDate { get; private set; }

    /// <summary>E.164, e.g. <c>+21698123456</c>.</summary>
    public string? Phone { get; private set; }

    /// <summary>Lower case.</summary>
    public string? Email { get; private set; }

    public PostalAddress? Address { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset LastModifiedAtUtc { get; private set; }

    /// <summary>End of the last closed rental of the person (reported by Bookings): document images are kept from there.</summary>
    public DateTimeOffset? LastRentalClosedAtUtc { get; private set; }

    public DateTimeOffset? AnonymizedAtUtc { get; private set; }

    /// <summary>Current and superseded documents.</summary>
    public IReadOnlyList<IdentityDocument> Documents => _documents;

    public IReadOnlyList<Consent> Consents => _consents;

    public IReadOnlyList<BlacklistEntry> BlacklistEntries => _blacklistEntries;

    public bool IsAnonymized => AnonymizedAtUtc is not null;

    public bool IsBlacklisted => _blacklistEntries.Exists(entry => entry.IsActive);

    public BlacklistEntry? ActiveBlacklistEntry => _blacklistEntries.Find(entry => entry.IsActive);

    public string FullName => string.Join(' ', new[] { FirstName, LastName }.Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>Documents not superseded by a newer one (at most one per type).</summary>
    public IReadOnlyList<IdentityDocument> CurrentDocuments => _documents.Where(document => document.IsCurrent).ToList();

    /// <summary>
    /// A new person; <paramref name="id"/> lets a caller that stores files before the transaction (public submission,
    /// ADR-0050) choose the identifier the file paths are made of, and keep it when the transaction is replayed.
    /// </summary>
    public static Result<Person> Register(PersonDetails details, DateTimeOffset now, Guid? id = null)
    {
        var facts = PersonFacts.From(details, TunisTime.DateOf(now));
        if (facts.IsFailure)
        {
            return facts.Error;
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A person id cannot be empty.", nameof(id));
        }

        var person = new Person(id ?? NewId(), facts.Value, now);
        person.Raise(new PersonRegistered(person.Id));
        return person;
    }

    public IdentityDocument? FindDocument(Guid documentId) => _documents.Find(document => document.Id == documentId);

    public IdentityDocument? CurrentDocument(IdentityDocumentType type) =>
        _documents.Find(document => document.IsCurrent && document.Type == type);

    public bool HasConsent(ConsentPurpose purpose) => _consents.Exists(consent => consent.Purpose == purpose && consent.IsActive);

    public Result UpdateDetails(PersonDetails details, DateTimeOffset now)
    {
        if (IsAnonymized)
        {
            return CustomersErrors.PersonAnonymized;
        }

        var facts = PersonFacts.From(details, TunisTime.DateOf(now));
        if (facts.IsFailure)
        {
            return facts.Error;
        }

        Apply(facts.Value);
        Touch(now);
        Raise(new PersonDetailsChanged(Id));
        return Result.Success();
    }

    /// <summary>
    /// Fills the missing nationality, birth date, phone, e-mail and address from <paramref name="details"/> (a person
    /// found again by the deduplication); existing values are never overwritten. Returns whether anything changed.
    /// </summary>
    public bool CompleteMissingDetails(PersonDetails details, DateTimeOffset now)
    {
        if (IsAnonymized)
        {
            return false;
        }

        var facts = PersonFacts.From(details, TunisTime.DateOf(now));
        if (facts.IsFailure)
        {
            return false;
        }

        var known = facts.Value;
        var changed = false;
        if (Nationality is null && known.Nationality is not null)
        {
            Nationality = known.Nationality;
            changed = true;
        }

        if (BirthDate is null && known.BirthDate is not null)
        {
            BirthDate = known.BirthDate;
            changed = true;
        }

        if (Phone is null && known.Phone is not null)
        {
            Phone = known.Phone;
            changed = true;
        }

        if (Email is null && known.Email is not null)
        {
            Email = known.Email;
            changed = true;
        }

        if (Address is null && known.Address is not null)
        {
            Address = known.Address;
            changed = true;
        }

        if (changed)
        {
            Touch(now);
            Raise(new PersonDetailsChanged(Id));
        }

        return changed;
    }

    /// <summary>
    /// Adds a document. A different number supersedes the current document of the same type (kept as history); the same
    /// number and issuing country corrects the current document instead (its verification is reset).
    /// </summary>
    public Result<IdentityDocument> AddDocument(IdentityDocumentDetails details, DateTimeOffset now)
    {
        if (IsAnonymized)
        {
            return CustomersErrors.PersonAnonymized;
        }

        var facts = IdentityDocumentFacts.From(details, TunisTime.DateOf(now));
        if (facts.IsFailure)
        {
            return facts.Error;
        }

        var current = CurrentDocument(facts.Value.Type);
        if (current is not null && current.HasSameNumber(facts.Value))
        {
            current.Correct(facts.Value);
            Touch(now);
            return current;
        }

        current?.Supersede(now);
        var document = IdentityDocument.Create(Id, facts.Value, now);
        _documents.Add(document);
        Touch(now);
        Raise(new IdentityDocumentAdded(Id, document.Id, document.Type));
        return document;
    }

    /// <summary>Corrects a current document (typing mistake, dates); its type cannot change and its verification is reset.</summary>
    public Result CorrectDocument(Guid documentId, IdentityDocumentDetails details, DateTimeOffset now)
    {
        var document = CurrentDocumentById(documentId);
        if (document.IsFailure)
        {
            return document.Error;
        }

        var facts = IdentityDocumentFacts.From(details, TunisTime.DateOf(now));
        if (facts.IsFailure)
        {
            return facts.Error;
        }

        if (facts.Value.Type != document.Value.Type)
        {
            return CustomersErrors.DocumentTypeInvalid;
        }

        document.Value.Correct(facts.Value);
        Touch(now);
        return Result.Success();
    }

    /// <summary>Removes a document entered by mistake; its images are discarded.</summary>
    public Result RemoveDocument(Guid documentId, DateTimeOffset now)
    {
        if (IsAnonymized)
        {
            return CustomersErrors.PersonAnonymized;
        }

        var document = FindDocument(documentId);
        if (document is null)
        {
            return CustomersErrors.DocumentNotFound;
        }

        _documents.Remove(document);
        Discard(document.Id, document.ImageFiles());
        Touch(now);
        return Result.Success();
    }

    /// <summary>Attaches the image of one side of a current document; a previous image of that side is discarded.</summary>
    public Result AttachDocumentImage(Guid documentId, DocumentImageSide side, DocumentImage image, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!IsAnonymized && !Enum.IsDefined(side))
        {
            return CustomersErrors.ImageSideInvalid;
        }

        var document = CurrentDocumentById(documentId);
        if (document.IsFailure)
        {
            return document.Error;
        }

        var previous = document.Value.ReplaceImage(side, image);
        if (previous is not null && !string.Equals(previous.Path, image.Path, StringComparison.Ordinal))
        {
            Discard(document.Value.Id, [previous]);
        }

        Touch(now);
        return Result.Success();
    }

    /// <summary>Records that <paramref name="verifier"/> checked the document against the original.</summary>
    public Result VerifyDocument(Guid documentId, AuditActor verifier, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        var document = CurrentDocumentById(documentId);
        if (document.IsFailure)
        {
            return document.Error;
        }

        document.Value.Verify(verifier, now);
        Touch(now);
        Raise(new IdentityDocumentVerified(Id, document.Value.Id));
        return Result.Success();
    }

    /// <summary>Gives the consent for <paramref name="purpose"/> (no change when it is already given).</summary>
    public Result GiveConsent(ConsentPurpose purpose, ConsentChannel channel, DateTimeOffset now)
    {
        var valid = ValidateConsent(purpose, channel);
        if (valid.IsFailure)
        {
            return valid;
        }

        if (HasConsent(purpose))
        {
            return Result.Success();
        }

        _consents.Add(Consent.Give(Id, purpose, channel, now));
        Touch(now);
        Raise(new ConsentChanged(Id, purpose, IsGiven: true));
        return Result.Success();
    }

    /// <summary>Withdraws the consent for <paramref name="purpose"/> (no change when it is not given).</summary>
    public Result WithdrawConsent(ConsentPurpose purpose, ConsentChannel channel, DateTimeOffset now)
    {
        var valid = ValidateConsent(purpose, channel);
        if (valid.IsFailure)
        {
            return valid;
        }

        var active = _consents.Find(consent => consent.Purpose == purpose && consent.IsActive);
        if (active is null)
        {
            return Result.Success();
        }

        active.Withdraw(channel, now);
        Touch(now);
        Raise(new ConsentChanged(Id, purpose, IsGiven: false));
        return Result.Success();
    }

    public Result Blacklist(string reason, AuditActor author, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(author);
        if (IsAnonymized)
        {
            return CustomersErrors.PersonAnonymized;
        }

        if (BlacklistEntry.CleanReason(reason) is not { } cleaned)
        {
            return CustomersErrors.BlacklistReasonInvalid;
        }

        if (IsBlacklisted)
        {
            return CustomersErrors.AlreadyBlacklisted;
        }

        _blacklistEntries.Add(BlacklistEntry.Add(Id, cleaned, author, now));
        Touch(now);
        Raise(new PersonBlacklisted(Id));
        return Result.Success();
    }

    public Result LiftBlacklist(string reason, AuditActor author, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(author);
        if (IsAnonymized)
        {
            return CustomersErrors.PersonAnonymized;
        }

        if (BlacklistEntry.CleanReason(reason) is not { } cleaned)
        {
            return CustomersErrors.BlacklistReasonInvalid;
        }

        if (ActiveBlacklistEntry is not { } active)
        {
            return CustomersErrors.NotBlacklisted;
        }

        active.Lift(cleaned, author, now);
        Touch(now);
        Raise(new PersonBlacklistLifted(Id));
        return Result.Success();
    }

    /// <summary>
    /// Data subject right: scrubs every personal data (names, birth date, nationality, contact details, address),
    /// removes the documents (their images are discarded), consents and blacklist history, and keeps the identifier
    /// referenced by bookings and invoices. A blacklisted person must first be lifted from the blacklist.
    /// </summary>
    public Result Anonymize(DateTimeOffset now)
    {
        if (IsAnonymized)
        {
            return Result.Success();
        }

        if (IsBlacklisted)
        {
            return CustomersErrors.BlacklistedPersonCannotBeAnonymized;
        }

        foreach (var document in _documents)
        {
            Discard(document.Id, document.ImageFiles());
        }

        _documents.Clear();
        _consents.Clear();
        _blacklistEntries.Clear();
        FirstName = string.Empty;
        LastName = string.Empty;
        SearchName = string.Empty;
        Nationality = null;
        BirthDate = null;
        Phone = null;
        Email = null;
        Address = null;
        AnonymizedAtUtc = now;
        Touch(now);
        Raise(new PersonAnonymized(Id));
        return Result.Success();
    }

    /// <summary>
    /// Retention: removes the images whose reference date — the later of their upload and of the last closed rental —
    /// is before <paramref name="cutoff"/>. Returns the number of images removed (their files are discarded).
    /// </summary>
    public int PurgeDocumentImages(DateTimeOffset cutoff, DateTimeOffset now)
    {
        var purged = 0;
        foreach (var document in _documents)
        {
            var removed = document.RemoveImagesOlderThan(cutoff, LastRentalClosedAtUtc);
            if (removed.Count > 0)
            {
                purged += removed.Count;
                Discard(document.Id, removed);
            }
        }

        if (purged > 0)
        {
            Touch(now);
        }

        return purged;
    }

    /// <summary>A rental of the person was closed: the retention of its document images restarts from there.</summary>
    public void RecordRentalClosed(DateTimeOffset closedAtUtc)
    {
        var closed = closedAtUtc.ToUniversalTime();
        if (LastRentalClosedAtUtc is null || closed > LastRentalClosedAtUtc)
        {
            LastRentalClosedAtUtc = closed;
        }
    }

    private Result ValidateConsent(ConsentPurpose purpose, ConsentChannel channel)
    {
        if (IsAnonymized)
        {
            return CustomersErrors.PersonAnonymized;
        }

        if (!Enum.IsDefined(purpose))
        {
            return CustomersErrors.ConsentPurposeInvalid;
        }

        return Enum.IsDefined(channel) ? Result.Success() : CustomersErrors.ConsentChannelInvalid;
    }

    private Result<IdentityDocument> CurrentDocumentById(Guid documentId)
    {
        if (IsAnonymized)
        {
            return CustomersErrors.PersonAnonymized;
        }

        var document = FindDocument(documentId);
        if (document is null)
        {
            return CustomersErrors.DocumentNotFound;
        }

        return document.IsCurrent ? document : CustomersErrors.DocumentNotCurrent;
    }

    private void Discard(Guid documentId, IEnumerable<DocumentImage> images)
    {
        var paths = images.Select(image => image.Path).ToArray();
        if (paths.Length > 0)
        {
            Raise(new DocumentImagesDiscarded(Id, documentId, paths));
        }
    }

    private void Touch(DateTimeOffset now) => LastModifiedAtUtc = now;

    private void Apply(PersonFacts facts)
    {
        FirstName = facts.FirstName;
        LastName = facts.LastName;
        SearchName = facts.SearchName;
        Nationality = facts.Nationality;
        BirthDate = facts.BirthDate;
        Phone = facts.Phone;
        Email = facts.Email;
        Address = facts.Address;
    }
}

/// <summary>What the agent (or the customer) enters about a person. Blank optional values mean "unknown".</summary>
public sealed record PersonDetails(
    string FirstName,
    string LastName,
    string? Nationality = null,
    DateOnly? BirthDate = null,
    string? Phone = null,
    string? Email = null,
    PostalAddress? Address = null);

/// <summary>Validated and normalized person details.</summary>
internal sealed record PersonFacts(
    string FirstName,
    string LastName,
    string SearchName,
    string? Nationality,
    DateOnly? BirthDate,
    string? Phone,
    string? Email,
    PostalAddress? Address)
{
    private const int MaxAgeYears = 120;

    public static Result<PersonFacts> From(PersonDetails details, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(details);
        var firstName = TextRules.Clean(details.FirstName);
        if (!TextRules.IsValidName(firstName, Person.NameMaxLength))
        {
            return CustomersErrors.FirstNameInvalid;
        }

        var lastName = TextRules.Clean(details.LastName);
        if (!TextRules.IsValidName(lastName, Person.NameMaxLength))
        {
            return CustomersErrors.LastNameInvalid;
        }

        string? nationality = null;
        if (!string.IsNullOrWhiteSpace(details.Nationality))
        {
            var code = CountryCodes.Normalize(details.Nationality);
            if (code.IsFailure)
            {
                return code.Error;
            }

            nationality = code.Value;
        }

        if (details.BirthDate is { } birthDate && (birthDate > today || birthDate < today.AddYears(-MaxAgeYears)))
        {
            return CustomersErrors.BirthDateInvalid;
        }

        string? phone = null;
        if (!string.IsNullOrWhiteSpace(details.Phone))
        {
            var normalized = PhoneNumbers.Normalize(details.Phone);
            if (normalized.IsFailure)
            {
                return normalized.Error;
            }

            phone = normalized.Value;
        }

        string? email = null;
        if (!string.IsNullOrWhiteSpace(details.Email))
        {
            var normalized = EmailAddresses.Normalize(details.Email);
            if (normalized.IsFailure)
            {
                return normalized.Error;
            }

            email = normalized.Value;
        }

        return new PersonFacts(
            firstName!,
            lastName!,
            SearchText.Normalize($"{firstName} {lastName}", Person.SearchNameMaxLength),
            nationality,
            details.BirthDate,
            phone,
            email,
            details.Address);
    }
}
