using System.Data;
using System.Globalization;
using CarRental.Application.Common.Concurrency;
using CarRental.Domain.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CarRental.Infrastructure.Persistence.Concurrency;

/// <summary>
/// <c>sp_getapplock @Resource = 'availability:{categoryId}', @LockMode = 'Exclusive', @LockOwner = 'Transaction'</c>
/// on the connection and transaction of the current unit of work (ADR-0006).
/// </summary>
internal sealed class SqlAvailabilityLock(CarRentalDbContext db) : IAvailabilityLock
{
    private const string Sql = """
        DECLARE @result int;
        EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @timeout;
        SELECT @result;
        """;

    public static string ResourceFor(Guid categoryId) =>
        string.Create(CultureInfo.InvariantCulture, $"availability:{categoryId:D}");

    public async Task<Result> AcquireAsync(Guid categoryId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (categoryId == Guid.Empty)
        {
            throw new ArgumentException("A category id is required.", nameof(categoryId));
        }

        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "The availability lock is owned by a transaction: acquire it inside IUnitOfWork.ExecuteAsync.");

        var wait = timeout ?? IAvailabilityLock.DefaultTimeout;
        var timeoutMilliseconds = (int)Math.Clamp(wait.TotalMilliseconds, 0, int.MaxValue);

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = Sql;
        command.CommandTimeout = (int)Math.Ceiling(wait.TotalSeconds) + 30;
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = ResourceFor(categoryId) });
        command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = timeoutMilliseconds });

        var outcome = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return outcome switch
        {
            0 or 1 => Result.Success(),
            -1 => AvailabilityLockErrors.Timeout,
            -3 => AvailabilityLockErrors.Deadlock,
            _ => AvailabilityLockErrors.Failed,
        };
    }
}
