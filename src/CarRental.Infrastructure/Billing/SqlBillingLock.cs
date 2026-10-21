using System.Data;
using System.Globalization;
using CarRental.Application.Billing.Invoicing;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CarRental.Infrastructure.Billing;

/// <summary>
/// <c>sp_getapplock @Resource = 'billing:…', @LockMode = 'Exclusive', @LockOwner = 'Transaction'</c> on the connection and
/// transaction of the current unit of work (same mechanism as the availability lock, ADR-0006).
/// </summary>
internal sealed class SqlBillingLock(CarRentalDbContext db) : IBillingLock
{
    private const string Sql = """
        DECLARE @result int;
        EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @timeout;
        SELECT @result;
        """;

    public async Task<Result> AcquireAsync(string resource, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        if (!resource.StartsWith("billing:", StringComparison.Ordinal) || resource.Length > 255)
        {
            throw new ArgumentException("A billing lock is named billing:… (see BillingLocks).", nameof(resource));
        }

        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("The billing lock is owned by a transaction: acquire it inside IUnitOfWork.ExecuteAsync.");

        var wait = timeout ?? IBillingLock.DefaultTimeout;
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = Sql;
        command.CommandTimeout = (int)Math.Ceiling(wait.TotalSeconds) + 30;
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource });
        command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = (int)Math.Clamp(wait.TotalMilliseconds, 0, int.MaxValue) });

        var outcome = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return outcome is 0 or 1 ? Result.Success() : BillingErrors.LockTimeout;
    }
}
