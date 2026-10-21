using System.Linq.Expressions;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Customers.Eligibility;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Customers.Queries;

internal sealed class CustomersQueries(IAppDbContext db, IUserContext userContext, DriverEligibilityChecker eligibility) : ICustomersQueries
{
    private const int MaxLimit = 100;
    private const int MaxPersonsPerPhone = 20;

    public async Task<IReadOnlyList<PersonSummaryInfo>> SearchPersonsAsync(string term, int limit = 20, CancellationToken cancellationToken = default)
    {
        if (PersonSearch.PredicateFor(term) is not { } predicate)
        {
            return [];
        }

        return await db.Set<Person>()
            .AsNoTracking()
            .Where(predicate)
            .OrderBy(p => p.LastName)
            .ThenBy(p => p.FirstName)
            .ThenBy(p => p.Id)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .Select(p => new PersonSummaryInfo(
                p.Id,
                p.FirstName,
                p.LastName,
                p.Nationality,
                p.BirthDate,
                p.Phone,
                p.Email,
                p.Documents.Count(d => d.SupersededAtUtc == null)))
            .ToListAsync(cancellationToken);
    }

    public async Task<PersonInfo?> GetPersonAsync(Guid personId, CancellationToken cancellationToken = default)
    {
        var person = await db.Set<Person>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == personId, cancellationToken);
        if (person is null)
        {
            return null;
        }

        var customerId = await db.Set<Customer>()
            .AsNoTracking()
            .Where(c => c.PersonId == personId)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var revealNumbers = userContext.HasPermission(Permissions.IdentityDocumentsRead);
        return new PersonInfo(
            person.Id,
            person.FirstName,
            person.LastName,
            person.Nationality,
            person.BirthDate,
            person.Phone,
            person.Email,
            person.Address.ToInfo(),
            person.IsAnonymized,
            customerId,
            [.. person.CurrentDocuments.OrderBy(d => d.Type).Select(d => d.ToInfo(revealNumbers))]);
    }

    public async Task<CustomerInfo?> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var customer = await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken);
        return customer is null ? null : await ToInfoAsync(customer, cancellationToken);
    }

    public async Task<CustomerInfo?> FindIndividualCustomerAsync(Guid personId, CancellationToken cancellationToken = default)
    {
        var customer = await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.PersonId == personId, cancellationToken);
        return customer is null ? null : await ToInfoAsync(customer, cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerInfo>> SearchCompaniesAsync(string term, int limit = 20, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        var companies = await CompanySearch.Apply(db.Set<Customer>().AsNoTracking(), term)
            .OrderBy(c => c.LegalName)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .ToListAsync(cancellationToken);
        return [.. companies.Select(ToCompanyInfo)];
    }

    public async Task<bool> IsBlacklistedAsync(Guid personId, CancellationToken cancellationToken = default) =>
        await db.Set<Person>()
            .AsNoTracking()
            .Where(p => p.Id == personId)
            .AnyAsync(p => p.BlacklistEntries.Any(e => e.LiftedAtUtc == null), cancellationToken);

    public async Task<IReadOnlyList<PersonSummaryInfo>> FindPersonsByPhoneAsync(string phone, CancellationToken cancellationToken = default)
    {
        var normalized = PhoneNumbers.Normalize(phone);
        if (normalized.IsFailure)
        {
            return [];
        }

        var number = normalized.Value;
        return await db.Set<Person>()
            .AsNoTracking()
            .Where(p => p.Phone == number && p.AnonymizedAtUtc == null)
            .OrderBy(p => p.LastName)
            .ThenBy(p => p.FirstName)
            .ThenBy(p => p.Id)
            .Take(MaxPersonsPerPhone)
            .Select(p => new PersonSummaryInfo(
                p.Id,
                p.FirstName,
                p.LastName,
                p.Nationality,
                p.BirthDate,
                p.Phone,
                p.Email,
                p.Documents.Count(d => d.SupersededAtUtc == null)))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> HasConsentAsync(Guid personId, ConsentPurposeInfo purpose, CancellationToken cancellationToken = default)
    {
        var domainPurpose = purpose.ToDomain();
        return await db.Set<Person>()
            .AsNoTracking()
            .Where(p => p.Id == personId && p.AnonymizedAtUtc == null)
            .AnyAsync(p => p.Consents.Any(c => c.Purpose == domainPurpose && c.WithdrawnAtUtc == null), cancellationToken);
    }

    public Task<Result<DriverEligibilityInfo>> CheckDriverEligibilityAsync(
        Guid personId,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        Guid? agencyId = null,
        CancellationToken cancellationToken = default) =>
        eligibility.CheckAsync(personId, pickupAt, returnAt, agencyId, cancellationToken);

    private static CustomerInfo ToCompanyInfo(Customer company) => new(
        company.Id,
        CustomerKindInfo.Company,
        company.LegalName ?? string.Empty,
        PersonId: null,
        company.LegalName,
        company.TaxIdentifier,
        company.BillingAddress.ToInfo(),
        company.Email,
        company.Phone);

    private async Task<CustomerInfo> ToInfoAsync(Customer customer, CancellationToken cancellationToken)
    {
        if (customer.Kind == CustomerKind.Company)
        {
            return ToCompanyInfo(customer);
        }

        var person = await db.Set<Person>()
            .AsNoTracking()
            .Where(p => p.Id == customer.PersonId)
            .Select(p => new { p.FirstName, p.LastName, p.Address, p.Email, p.Phone })
            .FirstOrDefaultAsync(cancellationToken);
        var name = person is null ? string.Empty : string.Join(' ', new[] { person.FirstName, person.LastName }.Where(n => n.Length > 0));
        return new CustomerInfo(
            customer.Id,
            CustomerKindInfo.Individual,
            name,
            customer.PersonId,
            LegalName: null,
            TaxIdentifier: null,
            person?.Address.ToInfo(),
            person?.Email,
            person?.Phone);
    }
}

/// <summary>Search of company payers by legal name (every word, accents and case ignored) or tax identifier (start).</summary>
internal static class CompanySearch
{
    private const int MinTaxIdentifierKeyLength = 3;

    public static IQueryable<Customer> Apply(IQueryable<Customer> companies, string? term)
    {
        var query = companies.Where(c => c.Kind == CustomerKind.Company);
        if (string.IsNullOrWhiteSpace(term))
        {
            return query;
        }

        Expression<Func<Customer, bool>> name = c => c.SearchName != null;
        foreach (var word in SearchText.Normalize(term).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            name = name.And(c => c.SearchName!.Contains(word));
        }

        var key = TaxIdentifiers.KeyOf(term);
        if (key.Length >= MinTaxIdentifierKeyLength && key.Any(char.IsAsciiDigit))
        {
            name = name.Or(c => c.TaxIdentifierKey != null && c.TaxIdentifierKey.StartsWith(key));
        }

        return query.Where(name);
    }
}
