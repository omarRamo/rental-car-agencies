using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Customers.Queries;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Customers.Administration;

/// <summary>Read side of the back office <c>/admin/customers</c> (permission <c>customers.read</c>).</summary>
public sealed class CustomerAdministrationQueries(
    IAppDbContext db,
    IUserContext userContext,
    IIdentityQueries identityQueries,
    ICustomersQueries customers)
{
    /// <summary>Persons matching <paramref name="term"/> (name, phone, document number, e-mail), or every person when blank.</summary>
    public async Task<Result<PagedResult<PersonListItem>>> SearchPersonsAsync(string? term, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!userContext.HasPermission(Permissions.CustomersRead))
        {
            return CustomersErrors.Forbidden;
        }

        var query = db.Set<Person>().AsNoTracking();
        query = PersonSearch.PredicateFor(term) is { } predicate
            ? query.Where(predicate)
            : query.Where(p => p.AnonymizedAtUtc == null);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(p => p.LastName)
            .ThenBy(p => p.FirstName)
            .ThenBy(p => p.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(p => new PersonListItem(
                p.Id,
                p.FirstName,
                p.LastName,
                p.Nationality,
                p.BirthDate,
                p.Phone,
                p.Email,
                p.Documents.Count(d => d.SupersededAtUtc == null),
                p.BlacklistEntries.Any(e => e.LiftedAtUtc == null)))
            .ToListAsync(cancellationToken);

        return new PagedResult<PersonListItem>(items, total, page.Page, page.PageSize);
    }

    public async Task<Result<PersonFile>> GetPersonFileAsync(Guid personId, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.CustomersRead))
        {
            return CustomersErrors.Forbidden;
        }

        var person = await db.Set<Person>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == personId, cancellationToken);
        if (person is null)
        {
            return CustomersErrors.PersonNotFound;
        }

        var customerId = await db.Set<Customer>()
            .AsNoTracking()
            .Where(c => c.PersonId == personId)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var userIds = person.Documents.Select(d => d.VerifiedByUserId)
            .Concat(person.BlacklistEntries.Select(e => e.AddedByUserId))
            .Concat(person.BlacklistEntries.Select(e => e.LiftedByUserId))
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var names = await identityQueries.GetDisplayNamesAsync(userIds, cancellationToken);
        string? NameOf(Guid? userId, string? stored) => userId is { } id && names.TryGetValue(id, out var name) ? name : stored;

        var revealNumbers = userContext.HasPermission(Permissions.IdentityDocumentsRead);
        var documents = person.Documents
            .OrderBy(d => d.SupersededAtUtc is null ? 0 : 1)
            .ThenBy(d => d.Type)
            .ThenByDescending(d => d.AddedAtUtc)
            .Select(d => new DocumentFileItem(
                d.Id,
                d.Type.ToInfo(),
                CustomerMapping.MaskNumber(d.Number, revealNumbers),
                d.IssuingCountry,
                d.IssuedOn,
                d.ExpiresOn,
                d.IsLatinScript,
                ImageInfo(d.FrontImage),
                ImageInfo(d.BackImage),
                d.AddedAtUtc,
                d.VerifiedAtUtc,
                NameOf(d.VerifiedByUserId, d.VerifiedByName),
                d.SupersededAtUtc))
            .ToList();
        var consents = person.Consents
            .OrderBy(c => c.Purpose)
            .ThenByDescending(c => c.GivenAtUtc)
            .Select(c => new ConsentItem(c.Id, c.Purpose.ToInfo(), c.GivenThrough.ToInfo(), c.GivenAtUtc, c.WithdrawnThrough?.ToInfo(), c.WithdrawnAtUtc))
            .ToList();
        var blacklist = person.BlacklistEntries
            .OrderByDescending(e => e.AddedAtUtc)
            .Select(e => new BlacklistItem(
                e.Id,
                e.Reason,
                e.AddedAtUtc,
                NameOf(e.AddedByUserId, e.AddedByName),
                e.LiftedAtUtc,
                NameOf(e.LiftedByUserId, e.LiftedByName),
                e.LiftReason))
            .ToList();

        return new PersonFile(
            person.Id,
            person.FirstName,
            person.LastName,
            person.Nationality,
            person.BirthDate,
            person.Phone,
            person.Email,
            person.Address.ToInfo(),
            person.CreatedAtUtc,
            person.AnonymizedAtUtc,
            customerId,
            documents,
            consents,
            blacklist,
            new PersonFilePermissions(
                CanManage: userContext.HasPermission(Permissions.CustomersManage) && !person.IsAnonymized,
                CanViewDocumentImages: revealNumbers,
                CanManagePrivacy: userContext.HasPermission(Permissions.CustomersPrivacy)));
    }

    /// <summary>Eligibility of the person for the given rental dates (platform settings).</summary>
    public async Task<Result<DriverEligibilityInfo>> CheckEligibilityAsync(
        Guid personId,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.CustomersRead))
        {
            return CustomersErrors.Forbidden;
        }

        return await customers.CheckDriverEligibilityAsync(personId, pickupAt, returnAt, agencyId: null, cancellationToken);
    }

    /// <summary>Company payers matching <paramref name="term"/> (legal name, tax identifier), or all when blank.</summary>
    public async Task<Result<PagedResult<CompanyListItem>>> SearchCompaniesAsync(string? term, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!userContext.HasPermission(Permissions.CustomersRead))
        {
            return CustomersErrors.Forbidden;
        }

        var query = CompanySearch.Apply(db.Set<Customer>().AsNoTracking(), term);
        var total = await query.CountAsync(cancellationToken);
        var companies = await query
            .OrderBy(c => c.LegalName)
            .ThenBy(c => c.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);
        var items = companies
            .Select(c => new CompanyListItem(c.Id, c.LegalName ?? string.Empty, c.TaxIdentifier ?? string.Empty, c.BillingAddress?.City ?? string.Empty, c.Email, c.Phone))
            .ToList();
        return new PagedResult<CompanyListItem>(items, total, page.Page, page.PageSize);
    }

    public async Task<Result<CompanyFile>> GetCompanyAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.CustomersRead))
        {
            return CustomersErrors.Forbidden;
        }

        var company = await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken);
        if (company is null)
        {
            return CustomersErrors.CustomerNotFound;
        }

        if (company.Kind != CustomerKind.Company || company.BillingAddress is null)
        {
            return CustomersErrors.NotACompany;
        }

        return new CompanyFile(
            company.Id,
            company.LegalName ?? string.Empty,
            company.TaxIdentifier ?? string.Empty,
            company.BillingAddress.ToInfo()!,
            company.ContactName,
            company.Email,
            company.Phone,
            company.CreatedAtUtc);
    }

    private static DocumentImageInfo? ImageInfo(DocumentImage? image) =>
        image is null ? null : new DocumentImageInfo(image.ContentType, image.Length, image.UploadedAtUtc);
}
