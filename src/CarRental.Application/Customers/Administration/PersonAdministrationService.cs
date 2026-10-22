using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;

namespace CarRental.Application.Customers.Administration;

/// <summary>
/// Changes made from the person page of the back office (permission <c>customers.manage</c>): identity and contact
/// details, documents (add, correct, remove, verify, images), consents and blacklist. Every change is audited
/// automatically (personal data masked in the audit log).
/// </summary>
public sealed class PersonAdministrationService(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IUserContext userContext,
    ICustomerRegistration registration,
    TimeProvider timeProvider)
{
    /// <summary>Creates a person, or finds it again without duplicate (same document, or same phone or e-mail and name).</summary>
    public Task<Result<PersonRegistrationResult>> RegisterAsync(PersonRegistrationRequest request, CancellationToken cancellationToken = default) =>
        registration.FindOrCreatePersonAsync(request, cancellationToken);

    public async Task<Result> UpdateAsync(Guid personId, PersonDetailsForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!CanManage)
        {
            return CustomersErrors.Forbidden;
        }

        var details = CustomerMapping.ToDomain(form.FirstName, form.LastName, form.Nationality, form.BirthDate, form.Phone, form.Email, form.Address);
        if (details.IsFailure)
        {
            return details.Error;
        }

        return await ChangePersonAsync(personId, (person, now) => person.UpdateDetails(details.Value, now), cancellationToken);
    }

    /// <summary>Adds a document (a new number supersedes the current one of the same type). Returns the document id.</summary>
    public Task<Result<Guid>> AddDocumentAsync(Guid personId, IdentityDocumentInput document, CancellationToken cancellationToken = default) =>
        registration.AddOrReplaceDocumentAsync(personId, document, cancellationToken: cancellationToken);

    /// <summary>Corrects a current document (its verification is reset); the number must stay unique among persons.</summary>
    public async Task<Result> CorrectDocumentAsync(Guid personId, Guid documentId, IdentityDocumentInput document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!CanManage)
        {
            return CustomersErrors.Forbidden;
        }

        var details = document.ToDomain();
        return await unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var person = await db.LoadPersonAsync(personId, ct);
                if (person is null)
                {
                    return CustomersErrors.PersonNotFound;
                }

                if (await db.IsDocumentNumberUsedByAnotherPersonAsync(person, details, ct))
                {
                    return CustomersErrors.DocumentNumberInUse;
                }

                return person.CorrectDocument(documentId, details, timeProvider.GetUtcNow());
            },
            cancellationToken);
    }

    /// <summary>Removes a document entered by mistake; its images are deleted after the commit.</summary>
    public Task<Result> RemoveDocumentAsync(Guid personId, Guid documentId, CancellationToken cancellationToken = default) =>
        CanManage
            ? ChangePersonAsync(personId, (person, now) => person.RemoveDocument(documentId, now), cancellationToken)
            : Task.FromResult<Result>(CustomersErrors.Forbidden);

    /// <summary>Marks the document as checked against the original by the current user.</summary>
    public Task<Result> VerifyDocumentAsync(Guid personId, Guid documentId, CancellationToken cancellationToken = default) =>
        CanManage
            ? ChangePersonAsync(personId, (person, now) => person.VerifyDocument(documentId, userContext.ToAuditActor(), now), cancellationToken)
            : Task.FromResult<Result>(CustomersErrors.Forbidden);

    /// <summary>Stores the image of one side of a document (photo from the tablet camera or file).</summary>
    public Task<Result> UploadDocumentImageAsync(Guid documentId, DocumentImageSideInfo side, Stream content, CancellationToken cancellationToken = default) =>
        registration.UploadDocumentImageAsync(documentId, side, content, cancellationToken);

    public Task<Result> SetConsentAsync(
        Guid personId,
        ConsentPurposeInfo purpose,
        ConsentChannelInfo channel,
        bool given,
        CancellationToken cancellationToken = default) =>
        registration.SetConsentAsync(personId, purpose, channel, given, cancellationToken);

    /// <summary>Blacklists the person with a mandatory reason (never shown to the person).</summary>
    public Task<Result> BlacklistAsync(Guid personId, string reason, CancellationToken cancellationToken = default) =>
        CanManage
            ? ChangePersonAsync(personId, (person, now) => person.Blacklist(reason, userContext.ToAuditActor(), now), cancellationToken)
            : Task.FromResult<Result>(CustomersErrors.Forbidden);

    /// <summary>Lifts the blacklist with a mandatory reason.</summary>
    public Task<Result> LiftBlacklistAsync(Guid personId, string reason, CancellationToken cancellationToken = default) =>
        CanManage
            ? ChangePersonAsync(personId, (person, now) => person.LiftBlacklist(reason, userContext.ToAuditActor(), now), cancellationToken)
            : Task.FromResult<Result>(CustomersErrors.Forbidden);

    private bool CanManage => userContext.HasPermission(Permissions.CustomersManage);

    private Task<Result> ChangePersonAsync(Guid personId, Func<Person, DateTimeOffset, Result> change, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var person = await db.LoadPersonAsync(personId, ct);
                return person is null ? CustomersErrors.PersonNotFound : change(person, timeProvider.GetUtcNow());
            },
            cancellationToken);
}
