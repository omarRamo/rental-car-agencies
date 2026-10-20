using CarRental.Application.Common.Persistence;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Payments;

/// <summary>Data access helpers of the Payments module.</summary>
internal static class PaymentPersistence
{
    /// <summary>Runs the operation in a unit of work; a concurrent change of the same record becomes a business conflict.</summary>
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
            return PaymentsErrors.ConcurrentUpdate;
        }
    }

    /// <inheritdoc cref="ExecuteGuardedAsync{T}(IUnitOfWork, Func{CancellationToken, Task{Result{T}}}, CancellationToken)"/>
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
            return PaymentsErrors.ConcurrentUpdate;
        }
    }

    /// <summary>True when <paramref name="agencyId"/> is an active agency of the Referential module.</summary>
    public static async Task<bool> IsActiveAgencyAsync(this IReferentialQueries referential, Guid agencyId, CancellationToken cancellationToken) =>
        (await referential.GetAgenciesAsync(cancellationToken)).Any(a => a.Id == agencyId && a.IsActive);

    public static Task<Payment?> FindPaymentAsync(this IAppDbContext db, Guid paymentId, CancellationToken cancellationToken) =>
        db.Set<Payment>().FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

    public static Task<DepositHold?> FindDepositAsync(this IAppDbContext db, Guid depositId, CancellationToken cancellationToken) =>
        db.Set<DepositHold>().FirstOrDefaultAsync(d => d.Id == depositId, cancellationToken);
}
