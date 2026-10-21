using System.Data;
using System.Globalization;
using CarRental.Application.Accounting.Periods;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Infrastructure.Accounting;

/// <summary>
/// <c>sp_getapplock @Resource = 'accounting:period:…', @LockMode = 'Shared' | 'Exclusive', @LockOwner = 'Transaction'</c>
/// on the connection and transaction of the current unit of work (same mechanism as the availability and billing locks).
/// The writers that date a fact in a month share it; the closing and the reopening of the month hold it alone.
/// </summary>
internal sealed class SqlAccountingPeriodLock(CarRentalDbContext db) : IAccountingPeriodLock
{
    private const string Sql = """
        DECLARE @result int;
        EXEC @result = sp_getapplock @Resource = @resource, @LockMode = @mode, @LockOwner = 'Transaction', @LockTimeout = @timeout;
        SELECT @result;
        """;

    public Task<Result> AcquireSharedAsync(string resource, CancellationToken cancellationToken = default) =>
        AcquireAsync(resource, "Shared", cancellationToken);

    public Task<Result> AcquireExclusiveAsync(string resource, CancellationToken cancellationToken = default) =>
        AcquireAsync(resource, "Exclusive", cancellationToken);

    private async Task<Result> AcquireAsync(string resource, string mode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        if (!resource.StartsWith("accounting:", StringComparison.Ordinal) || resource.Length > 255)
        {
            throw new ArgumentException("An accounting lock is named accounting:… (see AccountingLocks).", nameof(resource));
        }

        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("The accounting lock is owned by a transaction: acquire it inside IUnitOfWork.ExecuteAsync.");

        var wait = IAccountingPeriodLock.DefaultTimeout;
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = Sql;
        command.CommandTimeout = (int)Math.Ceiling(wait.TotalSeconds) + 30;
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource });
        command.Parameters.Add(new SqlParameter("@mode", SqlDbType.VarChar, 32) { Value = mode });
        command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = (int)wait.TotalMilliseconds });

        var outcome = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return outcome is 0 or 1 ? Result.Success() : AccountingErrors.LockTimeout;
    }
}

/// <summary>Composition of the Accounting module's adapters: the lock of the accounting months on SQL Server.</summary>
public static class AccountingSetup
{
    public static IServiceCollection AddAccountingInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IAccountingPeriodLock, SqlAccountingPeriodLock>();
        return services;
    }
}
