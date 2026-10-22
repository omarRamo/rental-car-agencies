using CarRental.Application.Customers.Contracts;
using CarRental.Application.Customers.Documents;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Customers.Registration;

/// <summary>
/// Public submissions (ADR-0050): images kept aside before a request, drivers submitted by visitors and the images sent
/// from "Ma réservation". A person already known is never changed by a public page: its submission waits for an agent.
/// </summary>
internal sealed partial class CustomerRegistration
{
    /// <summary>Two sides of four documents at most.</summary>
    private const int MaxSubmittedImages = 8;

    public async Task<Result<StagedDocumentImageInfo>> StageDocumentImageAsync(Stream content, Guid? agencyId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!CanManage)
        {
            return CustomersErrors.Forbidden;
        }

        var agency = AgencyFor(agencyId);
        if (agency.IsFailure)
        {
            return agency.Error;
        }

        var prepared = await DocumentImageStore.PrepareAsync(content, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error;
        }

        var staged = await imageStore.StageAsync(agency.Value, prepared.Value, cancellationToken);
        try
        {
            db.Set<StagedDocumentImage>().Add(staged);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await imageStore.DeleteStagedAsync(staged.Image.Path);
            throw;
        }

        return new StagedDocumentImageInfo(staged.Id, staged.Image.ContentType, staged.Image.Length);
    }

    public async Task<Result<SubmittedDriverResult>> RegisterSubmittedDriverAsync(SubmittedDriverRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Driver);
        ArgumentNullException.ThrowIfNull(request.Images);
        if (!CanManage)
        {
            return CustomersErrors.Forbidden;
        }

        if (request.BookingId == Guid.Empty)
        {
            throw new ArgumentException("A submission is for a booking.", nameof(request));
        }

        var agency = AgencyFor(request.AgencyId);
        if (agency.IsFailure)
        {
            return agency.Error;
        }

        var agencyId = agency.Value;
        var driver = request.Driver;
        var details = new PersonDetails(driver.FirstName, driver.LastName, driver.Nationality, driver.BirthDate, driver.Phone, driver.Email);
        var documents = (driver.Documents ?? []).Select(d => d.ToDomain()).ToList();
        var now = timeProvider.GetUtcNow();
        var issues = DriverSubmissionRules.Validate(details, documents, TunisTime.DateOf(now));
        if (issues.Count > 0)
        {
            return issues[0].Error;
        }

        if (documents.Count > 0 && !request.DocumentProcessingConsent)
        {
            return CustomersErrors.SubmissionConsentRequired;
        }

        var images = request.Images.Where(i => i.FrontStagedImageId is not null || i.BackStagedImageId is not null).ToList();
        if (images.Any(i => documents.All(d => d.Type != i.Type.ToDomain())) || images.Select(i => i.Type).Distinct().Count() != images.Count)
        {
            return CustomersErrors.DocumentTypeInvalid;
        }

        var imageIds = images.SelectMany(i => new[] { i.FrontStagedImageId, i.BackStagedImageId }).OfType<Guid>().ToList();
        if (imageIds.Count > MaxSubmittedImages || imageIds.Distinct().Count() != imageIds.Count)
        {
            return CustomersErrors.StagedImageNotFound;
        }

        // Images sent on the site of the agency only (an image of another agency is unknown here).
        var staged = await db.Set<StagedDocumentImage>()
            .AsNoTracking()
            .Where(s => s.AgencyId == agencyId && imageIds.Contains(s.Id) && s.ConsumedAtUtc == null)
            .ToListAsync(cancellationToken);
        if (staged.Count != imageIds.Count)
        {
            return CustomersErrors.StagedImageNotFound;
        }

        // Where the person stands decides the folder of the images; it is decided again in the transaction.
        var newPersonId = Entity.NewId();
        var expected = await MatchSubmissionAsync(agencyId, details, documents, newPersonId, now, cancellationToken);
        if (expected.IsFailure)
        {
            return expected.Error;
        }

        var ownerId = expected.Value.Person?.Id ?? newPersonId;
        var copied = new Dictionary<Guid, DocumentImage>();
        try
        {
            foreach (var image in staged)
            {
                var copy = await imageStore.CopyStagedAsync(ownerId, image, cancellationToken);
                if (copy.IsFailure)
                {
                    await DeleteAsync(copied.Values);
                    return copy.Error;
                }

                copied[image.Id] = copy.Value;
            }

            var consents = new SubmittedConsents(request.DocumentProcessingConsent, request.WhatsAppNotificationsConsent, request.MarketingConsent);
            var result = await unitOfWork.ExecuteGuardedAsync<SubmittedDriverResult>(
                ct => RegisterSubmissionAsync(agencyId, request, details, documents, images, copied, consents, newPersonId, ownerId, ct),
                cancellationToken);
            if (result.IsFailure)
            {
                await DeleteAsync(copied.Values);
                return result;
            }

            foreach (var image in staged)
            {
                await imageStore.DeleteStagedAsync(image.Image.Path);
            }

            return result;
        }
        catch
        {
            await DeleteAsync(copied.Values);
            throw;
        }
    }

    public async Task<Result> SubmitDocumentImageAsync(DocumentImageSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!CanManage)
        {
            return CustomersErrors.Forbidden;
        }

        var type = submission.Type.ToDomain();
        var side = submission.Side.ToDomain();
        if (!Enum.IsDefined(type))
        {
            return CustomersErrors.DocumentTypeInvalid;
        }

        if (!Enum.IsDefined(side))
        {
            return CustomersErrors.ImageSideInvalid;
        }

        var channel = submission.Channel.ToDomain();
        var owner = await CheckOwnerAsync(p => p.Id == submission.PersonId, cancellationToken);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        // The image was sent on the site of the agency of the person (the agency of the booking).
        var ownerAgencyId = owner.Value.AgencyId;
        var staged = await db.Set<StagedDocumentImage>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.AgencyId == ownerAgencyId && s.Id == submission.StagedImageId && s.ConsumedAtUtc == null, cancellationToken);
        if (staged is null)
        {
            return CustomersErrors.StagedImageNotFound;
        }

        var copy = await imageStore.CopyStagedAsync(owner.Value.Id, staged, cancellationToken);
        if (copy.IsFailure)
        {
            return copy.Error;
        }

        var image = copy.Value;
        try
        {
            var result = await unitOfWork.ExecuteGuardedAsync(
                async ct =>
                {
                    var now = timeProvider.GetUtcNow();
                    var row = await db.Set<StagedDocumentImage>().FirstOrDefaultAsync(s => s.Id == submission.StagedImageId, ct);
                    var consumed = row?.MarkConsumed(now) ?? CustomersErrors.StagedImageNotFound;
                    if (consumed.IsFailure)
                    {
                        return consumed;
                    }

                    var pending = await db.Set<DriverSubmission>()
                        .Where(s => s.PersonId == owner.Value.Id && s.BookingId == submission.BookingId && s.State == DriverSubmissionState.Pending)
                        .OrderByDescending(s => s.SubmittedAtUtc)
                        .FirstOrDefaultAsync(ct);
                    if (pending is not null)
                    {
                        return pending.AddImage(type, side, image, now);
                    }

                    var created = DriverSubmission.Create(
                        ownerAgencyId,
                        owner.Value.Id,
                        submission.BookingId,
                        channel,
                        details: null,
                        SubmissionMatch.Booking,
                        nameDiffers: false,
                        SubmittedConsents.ImagesOnly,
                        [new SubmittedDocumentEntry(type, null, side == DocumentImageSide.Front ? image : null, side == DocumentImageSide.Back ? image : null)],
                        now);
                    if (created.IsFailure)
                    {
                        return created.Error;
                    }

                    db.Set<DriverSubmission>().Add(created.Value);
                    return Result.Success();
                },
                cancellationToken);
            if (result.IsFailure)
            {
                await imageStore.DeleteUnreferencedAsync(image.Path);
                return result;
            }

            await imageStore.DeleteStagedAsync(staged.Image.Path);
            return result;
        }
        catch
        {
            await imageStore.DeleteUnreferencedAsync(image.Path);
            throw;
        }
    }

    public async Task<Result> DiscardDriverSubmissionsAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return CustomersErrors.Forbidden;
        }

        return await unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var pending = await db.Set<DriverSubmission>()
                    .Where(s => s.BookingId == bookingId && s.State == DriverSubmissionState.Pending)
                    .ToListAsync(ct);
                var now = timeProvider.GetUtcNow();
                foreach (var submission in pending)
                {
                    var rejected = submission.Reject(userContext.ToAuditActor(), now);
                    if (rejected.IsFailure)
                    {
                        return rejected;
                    }
                }

                return Result.Success();
            },
            cancellationToken);
    }

    /// <summary>
    /// The person of the agency the typed driver designates, if any (ADR-0027 rules on every submitted document), without
    /// changing anything.
    /// </summary>
    private async Task<Result<PersonMatch>> MatchSubmissionAsync(
        Guid agencyId,
        PersonDetails details,
        IReadOnlyList<IdentityDocumentDetails> documents,
        Guid probeId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var probe = Person.Register(agencyId, details, now, probeId);
        if (probe.IsFailure)
        {
            return probe.Error;
        }

        var probeDocuments = new List<IdentityDocument>(documents.Count);
        foreach (var document in documents)
        {
            var added = probe.Value.AddDocument(document, now);
            if (added.IsFailure)
            {
                return added.Error;
            }

            probeDocuments.Add(added.Value);
        }

        var candidates = await FindSubmissionCandidatesAsync(probe.Value, probeDocuments, cancellationToken);
        foreach (var document in probeDocuments.OrderBy(d => d.Type))
        {
            var byDocument = PersonMatcher.Match(probe.Value, document, candidates);
            if (byDocument.Kind == PersonMatchKindInfo.Document)
            {
                return byDocument;
            }
        }

        var identity = probeDocuments.FirstOrDefault(d => d.Type is IdentityDocumentType.NationalId or IdentityDocumentType.Passport)
            ?? probeDocuments.FirstOrDefault();
        return PersonMatcher.Match(probe.Value, identity, candidates);
    }

    /// <summary>
    /// Persons of the agency of the probe sharing the phone, the e-mail or one of the current documents of the probe (not
    /// anonymized).
    /// </summary>
    private async Task<List<Person>> FindSubmissionCandidatesAsync(Person probe, IReadOnlyList<IdentityDocument> documents, CancellationToken cancellationToken)
    {
        var agencyId = probe.AgencyId;
        var phone = probe.Phone;
        var email = probe.Email;
        var keys = documents.Select(d => d.NumberKey).Distinct(StringComparer.Ordinal).ToList();
        var candidates = await db.Set<Person>()
            .AsNoTracking()
            .Where(p => p.AgencyId == agencyId
                        && p.AnonymizedAtUtc == null
                        && ((phone != null && p.Phone == phone)
                            || (email != null && p.Email == email)
                            || p.Documents.Any(d => d.SupersededAtUtc == null && keys.Contains(d.NumberKey))))
            .OrderBy(p => p.CreatedAtUtc)
            .Take(MaxCandidates)
            .ToListAsync(cancellationToken);

        // The key alone is not the document: keep the candidates whose type and country also match, or that share a contact.
        return
        [
            .. candidates.Where(c => (phone != null && c.Phone == phone)
                                     || (email != null && c.Email == email)
                                     || documents.Any(d => c.CurrentDocument(d.Type) is { } current
                                                           && current.NumberKey == d.NumberKey
                                                           && current.IssuingCountry == d.IssuingCountry)),
        ];
    }

    /// <summary>
    /// In the transaction: the person found again (left unchanged, the submission waits for an agent) or created with its
    /// documents, images and consents, and the submission record. A change of the match since the images were copied is a
    /// conflict (the visitor submits again).
    /// </summary>
    private async Task<Result<SubmittedDriverResult>> RegisterSubmissionAsync(
        Guid agencyId,
        SubmittedDriverRequest request,
        PersonDetails details,
        IReadOnlyList<IdentityDocumentDetails> documents,
        IReadOnlyList<SubmittedDocumentImages> images,
        IReadOnlyDictionary<Guid, DocumentImage> copied,
        SubmittedConsents consents,
        Guid newPersonId,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var stagedIds = copied.Keys.ToList();
        var rows = await db.Set<StagedDocumentImage>().Where(s => s.AgencyId == agencyId && stagedIds.Contains(s.Id)).ToListAsync(cancellationToken);
        if (rows.Count != stagedIds.Count)
        {
            return CustomersErrors.StagedImageNotFound;
        }

        foreach (var row in rows)
        {
            var consumed = row.MarkConsumed(now);
            if (consumed.IsFailure)
            {
                return consumed.Error;
            }
        }

        var match = await MatchSubmissionAsync(agencyId, details, documents, newPersonId, now, cancellationToken);
        if (match.IsFailure)
        {
            return match.Error;
        }

        if ((match.Value.Person?.Id ?? newPersonId) != ownerId)
        {
            return CustomersErrors.ConcurrentUpdate;
        }

        DocumentImage? ImageOf(IdentityDocumentType type, DocumentImageSide side)
        {
            var entry = images.FirstOrDefault(i => i.Type.ToDomain() == type);
            var id = side == DocumentImageSide.Front ? entry?.FrontStagedImageId : entry?.BackStagedImageId;
            return id is { } stagedId ? copied[stagedId] : null;
        }

        var entries = documents
            .Select(d => new SubmittedDocumentEntry(d.Type, d, ImageOf(d.Type, DocumentImageSide.Front), ImageOf(d.Type, DocumentImageSide.Back)))
            .ToList();
        var channel = request.Channel.ToDomain();
        if (match.Value.Person is { } known)
        {
            // A person already known is never changed by a public page: an agent reconciles the submission.
            var pending = DriverSubmission.Create(
                known.AgencyId,
                known.Id,
                request.BookingId,
                channel,
                details,
                ToSubmissionMatch(match.Value.Kind),
                match.Value.NameDiffers,
                consents,
                entries,
                now);
            if (pending.IsFailure)
            {
                return pending.Error;
            }

            db.Set<DriverSubmission>().Add(pending.Value);
            return new SubmittedDriverResult(known.Id, pending.Value.Id, RequiresReview: true);
        }

        var person = Person.Register(agencyId, details, now, newPersonId);
        if (person.IsFailure)
        {
            return person.Error;
        }

        foreach (var entry in entries)
        {
            var added = person.Value.AddDocument(entry.Details!, now);
            if (added.IsFailure)
            {
                return added.Error;
            }

            foreach (var (side, image) in new[] { (DocumentImageSide.Front, entry.FrontImage), (DocumentImageSide.Back, entry.BackImage) })
            {
                if (image is not null)
                {
                    var attached = person.Value.AttachDocumentImage(added.Value.Id, side, image, now);
                    if (attached.IsFailure)
                    {
                        return attached.Error;
                    }
                }
            }
        }

        var given = Result.Combine(
            consents.DocumentProcessing && entries.Count > 0 ? person.Value.GiveConsent(ConsentPurpose.IdentityDocumentProcessing, channel, now) : Result.Success(),
            consents.WhatsAppNotifications ? person.Value.GiveConsent(ConsentPurpose.WhatsAppNotifications, channel, now) : Result.Success(),
            consents.Marketing ? person.Value.GiveConsent(ConsentPurpose.MarketingEmail, channel, now) : Result.Success());
        if (given.IsFailure)
        {
            return given.Error;
        }

        var applied = DriverSubmission.Create(agencyId, person.Value.Id, request.BookingId, channel, details, SubmissionMatch.None, false, consents, entries, now);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        db.Set<Person>().Add(person.Value);
        db.Set<DriverSubmission>().Add(applied.Value);
        return new SubmittedDriverResult(person.Value.Id, applied.Value.Id, RequiresReview: false);
    }

    private static SubmissionMatch ToSubmissionMatch(PersonMatchKindInfo kind) => kind switch
    {
        PersonMatchKindInfo.Document => SubmissionMatch.Document,
        PersonMatchKindInfo.Phone => SubmissionMatch.Phone,
        PersonMatchKindInfo.Email => SubmissionMatch.Email,
        _ => SubmissionMatch.None,
    };
}
