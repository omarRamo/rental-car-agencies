using CarRental.Application.Common.Persistence;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Customers;

/// <summary>Data access helpers of the Customers module.</summary>
internal static class CustomerPersistence
{
    /// <summary>The whole person aggregate (documents, consents and blacklist entries are auto-included), tracked.</summary>
    public static Task<Person?> LoadPersonAsync(this IAppDbContext db, Guid personId, CancellationToken cancellationToken) =>
        db.Set<Person>().FirstOrDefaultAsync(p => p.Id == personId, cancellationToken);

    /// <summary>
    /// True when another person holds a current document with the same type, issuing country and number key as
    /// <paramref name="details"/>. Checked before any change; invalid details answer false (the domain rejects them).
    /// </summary>
    public static Task<bool> IsDocumentNumberUsedByAnotherPersonAsync(
        this IAppDbContext db,
        Guid personId,
        IdentityDocumentDetails details,
        CancellationToken cancellationToken)
    {
        var issuingCountry = CountryCodes.Normalize(details.IssuingCountry);
        var key = DocumentNumbers.KeyOf(details.Number);
        if (issuingCountry.IsFailure || key.Length == 0)
        {
            return Task.FromResult(false);
        }

        var type = details.Type;
        var country = issuingCountry.Value;
        return db.Set<Person>()
            .AsNoTracking()
            .AnyAsync(
                p => p.Id != personId
                     && p.Documents.Any(d => d.SupersededAtUtc == null && d.Type == type && d.IssuingCountry == country && d.NumberKey == key),
                cancellationToken);
    }

    /// <summary>Runs the operation in a unit of work; a concurrent change of the same record becomes a business conflict.</summary>
    public static async Task<Result> ExecuteGuardedAsync(
        this IUnitOfWork unitOfWork,
        Func<CancellationToken, Task<Result>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await unitOfWork.ExecuteAsync(operation, cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CustomersErrors.ConcurrentUpdate;
        }
    }

    /// <inheritdoc cref="ExecuteGuardedAsync(IUnitOfWork, Func{CancellationToken, Task{Result}}, CancellationToken)"/>
    public static async Task<Result<T>> ExecuteGuardedAsync<T>(
        this IUnitOfWork unitOfWork,
        Func<CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await unitOfWork.ExecuteAsync(operation, cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CustomersErrors.ConcurrentUpdate;
        }
    }
}
