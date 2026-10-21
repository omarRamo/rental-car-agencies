using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Customers.Administration;

/// <summary>A pending public submission as the agent reviews it: what the visitor typed, next to the person on file.</summary>
public sealed record DriverSubmissionView(
    Guid Id,
    Guid PersonId,
    Guid BookingId,
    DateTimeOffset SubmittedAtUtc,
    PersonMatchKindInfo? MatchedBy,
    bool NameDiffers,
    string? FirstName,
    string? LastName,
    string? Nationality,
    DateOnly? BirthDate,
    string? Phone,
    string? Email,
    bool WhatsAppNotificationsConsent,
    bool MarketingConsent,
    IReadOnlyList<SubmittedDocumentView> Documents);

/// <summary>A document of a pending submission (number masked unless the agent may read identity documents).</summary>
public sealed record SubmittedDocumentView(
    Guid Id,
    IdentityDocumentTypeInfo Type,
    string? Number,
    string? IssuingCountry,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    bool IsLatinScript,
    bool HasFrontImage,
    bool HasBackImage,
    bool FrontIsPdf = false,
    bool BackIsPdf = false);

/// <summary>
/// Reconciliation of the public submissions of a person already known (ADR-0050): the agent compares what the visitor
/// typed with the person on file, then accepts it — the documents are added to the person (a same number corrects the
/// current document and resets its verification), their images move to it, missing details and the consents are
/// completed, never overwritten — or rejects it (its images are deleted). Reading needs <c>customers.read</c>, deciding
/// <c>customers.manage</c>; the images are viewed through the logged document viewer (their ids are document ids).
/// </summary>
public sealed class DriverSubmissionReviewService(IAppDbContext db, IUnitOfWork unitOfWork, IUserContext userContext, TimeProvider timeProvider)
{
    /// <summary>The pending submissions of a person, oldest first.</summary>
    public async Task<Result<IReadOnlyList<DriverSubmissionView>>> GetPendingAsync(Guid personId, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.CustomersRead))
        {
            return CustomersErrors.Forbidden;
        }

        var submissions = await db.Set<DriverSubmission>()
            .AsNoTracking()
            .Where(s => s.PersonId == personId && s.State == DriverSubmissionState.Pending)
            .OrderBy(s => s.SubmittedAtUtc)
            .ToListAsync(cancellationToken);
        var reveal = userContext.HasPermission(Permissions.IdentityDocumentsRead);
        return Result.Success<IReadOnlyList<DriverSubmissionView>>(
        [
            .. submissions.Select(s => new DriverSubmissionView(
                s.Id,
                s.PersonId,
                s.BookingId,
                s.SubmittedAtUtc,
                s.Match switch
                {
                    SubmissionMatch.Document => PersonMatchKindInfo.Document,
                    SubmissionMatch.Phone => PersonMatchKindInfo.Phone,
                    SubmissionMatch.Email => PersonMatchKindInfo.Email,
                    _ => null,
                },
                s.NameDiffers,
                s.FirstName,
                s.LastName,
                s.Nationality,
                s.BirthDate,
                s.Phone,
                s.Email,
                s.WhatsAppNotificationsConsent,
                s.MarketingConsent,
                [
                    .. s.Documents.Select(d => new SubmittedDocumentView(
                        d.Id,
                        d.Type.ToInfo(),
                        d.Number is null ? null : CustomerMapping.MaskNumber(d.Number, reveal),
                        d.IssuingCountry,
                        d.IssuedOn,
                        d.ExpiresOn,
                        d.IsLatinScript,
                        d.FrontImage is not null,
                        d.BackImage is not null,
                        d.FrontImage?.ContentType == DocumentFileFormats.PdfContentType,
                        d.BackImage?.ContentType == DocumentFileFormats.PdfContentType)),
                ])),
        ]);
    }

    /// <summary>Applies a pending submission to its person (see the class remarks).</summary>
    public async Task<Result> AcceptAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.CustomersManage))
        {
            return CustomersErrors.Forbidden;
        }

        return await unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var submission = await db.Set<DriverSubmission>().FirstOrDefaultAsync(s => s.Id == submissionId, ct);
                if (submission is null)
                {
                    return CustomersErrors.SubmissionNotFound;
                }

                if (!submission.IsPending)
                {
                    return CustomersErrors.SubmissionNotPending;
                }

                var person = await db.LoadPersonAsync(submission.PersonId, ct);
                if (person is null)
                {
                    return CustomersErrors.PersonNotFound;
                }

                if (person.IsAnonymized)
                {
                    return CustomersErrors.PersonAnonymized;
                }

                var now = timeProvider.GetUtcNow();
                foreach (var document in submission.Documents)
                {
                    var applied = await ApplyAsync(person, document, now, ct);
                    if (applied.IsFailure)
                    {
                        return applied;
                    }
                }

                if (submission.Details is { } typed)
                {
                    person.CompleteMissingDetails(typed, now);
                }

                var consents = Result.Combine(
                    submission.DocumentProcessingConsent && submission.Documents.Count > 0
                        ? person.GiveConsent(ConsentPurpose.IdentityDocumentProcessing, submission.Channel, now)
                        : Result.Success(),
                    submission.WhatsAppNotificationsConsent ? person.GiveConsent(ConsentPurpose.WhatsAppNotifications, submission.Channel, now) : Result.Success(),
                    submission.MarketingConsent ? person.GiveConsent(ConsentPurpose.MarketingEmail, submission.Channel, now) : Result.Success());
                return consents.IsFailure ? consents : submission.Accept(userContext.ToAuditActor(), now);
            },
            cancellationToken);
    }

    /// <summary>Discards a pending submission; its images are deleted after the commit.</summary>
    public async Task<Result> RejectAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.CustomersManage))
        {
            return CustomersErrors.Forbidden;
        }

        return await unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var submission = await db.Set<DriverSubmission>().FirstOrDefaultAsync(s => s.Id == submissionId, ct);
                return submission is null
                    ? CustomersErrors.SubmissionNotFound
                    : submission.Reject(userContext.ToAuditActor(), timeProvider.GetUtcNow());
            },
            cancellationToken);
    }

    /// <summary>One submitted document onto the person: added (or correcting the same number), then its images attached.</summary>
    private async Task<Result> ApplyAsync(Person person, SubmittedDocument document, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IdentityDocument target;
        if (document.Details is { } details)
        {
            if (await db.IsDocumentNumberUsedByAnotherPersonAsync(person.Id, details, cancellationToken))
            {
                return CustomersErrors.DocumentNumberInUse;
            }

            var added = person.AddDocument(details, now);
            if (added.IsFailure)
            {
                return added.Error;
            }

            target = added.Value;
        }
        else if (person.CurrentDocument(document.Type) is { } current)
        {
            target = current;
        }
        else
        {
            return CustomersErrors.SubmissionDocumentMissing;
        }

        foreach (var side in new[] { DocumentImageSide.Front, DocumentImageSide.Back })
        {
            if (document.ImageOf(side) is { } image)
            {
                var attached = person.AttachDocumentImage(target.Id, side, image, now);
                if (attached.IsFailure)
                {
                    return attached;
                }
            }
        }

        return Result.Success();
    }
}
