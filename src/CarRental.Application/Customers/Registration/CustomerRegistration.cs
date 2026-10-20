using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Customers.Documents;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Customers.Registration;

internal sealed class CustomerRegistration(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IUserContext userContext,
    DocumentImageStore imageStore,
    TimeProvider timeProvider) : ICustomerRegistration
{
    /// <summary>Persons sharing a phone or an e-mail loaded for the comparison (an agency number typed for walk-ins…).</summary>
    private const int MaxCandidates = 50;

    private bool CanManage => userContext.HasPermission(Permissions.CustomersManage);

    public async Task<Result<PersonRegistrationResult>> FindOrCreatePersonAsync(
        PersonRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanManage)
        {
            return CustomersErrors.Forbidden;
        }

        var details = CustomerMapping.ToDomain(
            request.FirstName,
            request.LastName,
            request.Nationality,
            request.BirthDate,
            request.Phone,
            request.Email,
            request.Address);
        if (details.IsFailure)
        {
            return details.Error;
        }

        var document = request.Document?.ToDomain();

        // Invalid input fails before any database access; the person is built again inside the unit of work (replayed
        // on a transient failure).
        var validation = Probe(details.Value, document, timeProvider.GetUtcNow());
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        return await unitOfWork.ExecuteGuardedAsync(ct => RegisterAsync(details.Value, document, ct), cancellationToken);
    }

    public async Task<Result<Guid>> GetOrCreateIndividualCustomerAsync(Guid personId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return CustomersErrors.Forbidden;
        }

        return await unitOfWork.ExecuteGuardedAsync<Guid>(
            async ct =>
            {
                var person = await db.Set<Person>()
                    .AsNoTracking()
                    .Where(p => p.Id == personId)
                    .Select(p => new { p.AnonymizedAtUtc })
                    .FirstOrDefaultAsync(ct);
                if (person is null)
                {
                    return CustomersErrors.PersonNotFound;
                }

                if (person.AnonymizedAtUtc is not null)
                {
                    return CustomersErrors.PersonAnonymized;
                }

                var existing = await db.Set<Customer>()
                    .Where(c => c.PersonId == personId)
                    .Select(c => (Guid?)c.Id)
                    .FirstOrDefaultAsync(ct);
                if (existing is { } customerId)
                {
                    return customerId;
                }

                var customer = Customer.ForPerson(personId, timeProvider.GetUtcNow()).Value;
                db.Set<Customer>().Add(customer);
                return customer.Id;
            },
            cancellationToken);
    }

    public async Task<Result<Guid>> AddOrReplaceDocumentAsync(
        Guid personId,
        IdentityDocumentInput document,
        Stream? frontImage = null,
        Stream? backImage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!CanManage)
        {
            return CustomersErrors.Forbidden;
        }

        var details = document.ToDomain();
        var front = await PrepareAsync(frontImage, cancellationToken);
        if (front.IsFailure)
        {
            return front.Error;
        }

        var back = await PrepareAsync(backImage, cancellationToken);
        if (back.IsFailure)
        {
            return back.Error;
        }

        var owner = await CheckOwnerAsync(p => p.Id == personId, cancellationToken);
        if (owner.IsFailure)
        {
            return owner.Error;
        }

        // Files first (new paths, outside the transaction), references in the transaction, files deleted if it fails.
        var stored = new List<DocumentImage>(2);
        try
        {
            var frontStored = front.Value is { } f ? await imageStore.StoreAsync(personId, f, cancellationToken) : null;
            if (frontStored is not null)
            {
                stored.Add(frontStored);
            }

            var backStored = back.Value is { } b ? await imageStore.StoreAsync(personId, b, cancellationToken) : null;
            if (backStored is not null)
            {
                stored.Add(backStored);
            }

            var result = await unitOfWork.ExecuteGuardedAsync<Guid>(
                async ct =>
                {
                    var now = timeProvider.GetUtcNow();
                    var person = await db.LoadPersonAsync(personId, ct);
                    if (person is null)
                    {
                        return CustomersErrors.PersonNotFound;
                    }

                    if (await db.IsDocumentNumberUsedByAnotherPersonAsync(personId, details, ct))
                    {
                        return CustomersErrors.DocumentNumberInUse;
                    }

                    var added = person.AddDocument(details, now);
                    if (added.IsFailure)
                    {
                        return added.Error;
                    }

                    var attached = Result.Combine(
                        frontStored is null ? Result.Success() : person.AttachDocumentImage(added.Value.Id, DocumentImageSide.Front, frontStored, now),
                        backStored is null ? Result.Success() : person.AttachDocumentImage(added.Value.Id, DocumentImageSide.Back, backStored, now));
                    return attached.IsFailure ? attached.Error : added.Value.Id;
                },
                cancellationToken);

            if (result.IsFailure)
            {
                await DeleteAsync(stored);
            }

            return result;
        }
        catch
        {
            await DeleteAsync(stored);
            throw;
        }
    }

    public async Task<Result> UploadDocumentImageAsync(
        Guid documentId,
        DocumentImageSideInfo side,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!CanManage)
        {
            return CustomersErrors.Forbidden;
        }

        var domainSide = side.ToDomain();
        if (!Enum.IsDefined(domainSide))
        {
            return CustomersErrors.ImageSideInvalid;
        }

        var prepared = await DocumentImageStore.PrepareAsync(content, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error;
        }

        var owner = await CheckOwnerAsync(p => p.Documents.Any(d => d.Id == documentId), cancellationToken);
        if (owner.IsFailure)
        {
            return owner.Error.Code == CustomersErrors.PersonNotFound.Code ? CustomersErrors.DocumentNotFound : owner.Error;
        }

        var image = await imageStore.StoreAsync(owner.Value, prepared.Value, cancellationToken);
        try
        {
            var result = await unitOfWork.ExecuteGuardedAsync(
                async ct =>
                {
                    var person = await db.LoadPersonAsync(owner.Value, ct);
                    return person is null
                        ? CustomersErrors.DocumentNotFound
                        : person.AttachDocumentImage(documentId, domainSide, image, timeProvider.GetUtcNow());
                },
                cancellationToken);

            if (result.IsFailure)
            {
                await imageStore.DeleteUnreferencedAsync(image.Path);
            }

            return result;
        }
        catch
        {
            await imageStore.DeleteUnreferencedAsync(image.Path);
            throw;
        }
    }

    public async Task<Result<Guid>> CreateCompanyCustomerAsync(CompanyCustomerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanManage)
        {
            return CustomersErrors.Forbidden;
        }

        var details = request.ToDomain();
        if (details.IsFailure)
        {
            return details.Error;
        }

        return await unitOfWork.ExecuteGuardedAsync<Guid>(
            async ct =>
            {
                var company = Customer.ForCompany(details.Value, timeProvider.GetUtcNow());
                if (company.IsFailure)
                {
                    return company.Error;
                }

                var key = company.Value.TaxIdentifierKey;
                if (await db.Set<Customer>().AnyAsync(c => c.Kind == CustomerKind.Company && c.TaxIdentifierKey == key, ct))
                {
                    return CustomersErrors.CompanyAlreadyExists;
                }

                db.Set<Customer>().Add(company.Value);
                return company.Value.Id;
            },
            cancellationToken);
    }

    public async Task<Result> SetConsentAsync(
        Guid personId,
        ConsentPurposeInfo purpose,
        ConsentChannelInfo channel,
        bool given,
        CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return CustomersErrors.Forbidden;
        }

        return await unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var person = await db.LoadPersonAsync(personId, ct);
                if (person is null)
                {
                    return CustomersErrors.PersonNotFound;
                }

                var now = timeProvider.GetUtcNow();
                return given
                    ? person.GiveConsent(purpose.ToDomain(), channel.ToDomain(), now)
                    : person.WithdrawConsent(purpose.ToDomain(), channel.ToDomain(), now);
            },
            cancellationToken);
    }

    public Task<Result> RecordRentalClosedAsync(Guid personId, DateTimeOffset closedAtUtc, CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var person = await db.LoadPersonAsync(personId, ct);
                if (person is null)
                {
                    return CustomersErrors.PersonNotFound;
                }

                person.RecordRentalClosed(closedAtUtc);
                return Result.Success();
            },
            cancellationToken);

    private static Result Probe(PersonDetails details, IdentityDocumentDetails? document, DateTimeOffset now)
    {
        var person = Person.Register(details, now);
        if (person.IsFailure)
        {
            return person.Error;
        }

        return document is null ? Result.Success() : person.Value.AddDocument(document, now);
    }

    private static async Task<Result<PreparedDocumentImage?>> PrepareAsync(Stream? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return Result.Success<PreparedDocumentImage?>(null);
        }

        var prepared = await DocumentImageStore.PrepareAsync(content, cancellationToken);
        return prepared.IsSuccess ? Result.Success<PreparedDocumentImage?>(prepared.Value) : Result.Failure<PreparedDocumentImage?>(prepared.Error);
    }

    private async Task<Result<PersonRegistrationResult>> RegisterAsync(
        PersonDetails details,
        IdentityDocumentDetails? documentDetails,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var probe = Person.Register(details, now);
        if (probe.IsFailure)
        {
            return probe.Error;
        }

        IdentityDocument? probeDocument = null;
        if (documentDetails is not null)
        {
            var added = probe.Value.AddDocument(documentDetails, now);
            if (added.IsFailure)
            {
                return added.Error;
            }

            probeDocument = added.Value;
        }

        var candidates = await FindCandidatesAsync(probe.Value, probeDocument, cancellationToken);
        var match = PersonMatcher.Match(probe.Value, probeDocument, candidates);
        if (match.Person is { } existing)
        {
            existing.CompleteMissingDetails(details, now);
            if (documentDetails is not null && existing.CurrentDocument(documentDetails.Type) is null)
            {
                var added = existing.AddDocument(documentDetails, now);
                if (added.IsFailure)
                {
                    return added.Error;
                }
            }

            return new PersonRegistrationResult(existing.Id, match.Kind, match.NameDiffers, match.SimilarPersonIds);
        }

        db.Set<Person>().Add(probe.Value);
        return new PersonRegistrationResult(probe.Value.Id, PersonMatchKindInfo.Created, NameDiffers: false, match.SimilarPersonIds);
    }

    private async Task<List<Person>> FindCandidatesAsync(Person probe, IdentityDocument? document, CancellationToken cancellationToken)
    {
        var phone = probe.Phone;
        var email = probe.Email;
        var type = document?.Type;
        var country = document?.IssuingCountry;
        var key = document?.NumberKey;
        return await db.Set<Person>()
            .Where(p => p.AnonymizedAtUtc == null
                        && ((phone != null && p.Phone == phone)
                            || (email != null && p.Email == email)
                            || (key != null && p.Documents.Any(d => d.SupersededAtUtc == null
                                                                    && d.Type == type
                                                                    && d.IssuingCountry == country
                                                                    && d.NumberKey == key))))
            .OrderBy(p => p.CreatedAtUtc)
            .Take(MaxCandidates)
            .ToListAsync(cancellationToken);
    }

    /// <summary>The id of the (not anonymized) person matching <paramref name="predicate"/>.</summary>
    private async Task<Result<Guid>> CheckOwnerAsync(System.Linq.Expressions.Expression<Func<Person, bool>> predicate, CancellationToken cancellationToken)
    {
        var owner = await db.Set<Person>()
            .AsNoTracking()
            .Where(predicate)
            .Select(p => new { p.Id, p.AnonymizedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            return CustomersErrors.PersonNotFound;
        }

        return owner.AnonymizedAtUtc is null ? owner.Id : CustomersErrors.PersonAnonymized;
    }

    private async Task DeleteAsync(IEnumerable<DocumentImage> images)
    {
        foreach (var image in images)
        {
            await imageStore.DeleteUnreferencedAsync(image.Path);
        }
    }
}
