using System.Data;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Infrastructure.Persistence;

/// <summary>
/// <see cref="IUnitOfWork"/> on the scoped <see cref="CarRentalDbContext"/>. The transaction runs inside
/// <c>Database.CreateExecutionStrategy().ExecuteAsync</c>, as required by the retrying SQL Server strategy: on a transient
/// failure the whole operation is replayed on a clean change tracker. A nested call joins the ambient transaction.
/// </summary>
internal sealed class EfUnitOfWork(CarRentalDbContext db) : IUnitOfWork
{
    public bool HasActiveTransaction => db.Database.CurrentTransaction is not null;

    public Task<Result> ExecuteAsync(
        Func<CancellationToken, Task<Result>> operation,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteCoreAsync(operation, static result => result.IsSuccess, isolationLevel, cancellationToken);
    }

    public Task<Result<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<Result<T>>> operation,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteCoreAsync(operation, static result => result.IsSuccess, isolationLevel, cancellationToken);
    }

    private async Task<TResult> ExecuteCoreAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        Func<TResult, bool> isSuccess,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            // Nested unit of work: the outermost one commits or rolls back.
            var nested = await operation(cancellationToken);
            if (isSuccess(nested))
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            return nested;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var attempt = 0;
        try
        {
            return await strategy.ExecuteAsync(
                async ct =>
                {
                    if (attempt++ > 0)
                    {
                        // Retry after a transient failure: replay from a clean state.
                        db.ChangeTracker.Clear();
                    }

                    await using var transaction = await db.Database.BeginTransactionAsync(isolationLevel, ct);
                    var result = await operation(ct);
                    if (!isSuccess(result))
                    {
                        await transaction.RollbackAsync(ct);
                        db.ChangeTracker.Clear();
                        return result;
                    }

                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return result;
                },
                cancellationToken);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }
}
