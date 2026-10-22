using System.Data;
using System.Globalization;
using CarRental.Application.Cash.Expenses;
using CarRental.Domain.Cash.Expenses;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CarRental.Infrastructure.Accounting;

/// <summary>
/// <c>sp_getapplock @Resource = 'cash:expense-number:{agency}:{year}', @LockMode = 'Exclusive', @LockOwner = 'Transaction'</c>
/// on the connection and transaction of the current unit of work (same mechanism as the billing and accounting locks).
/// </summary>
internal sealed class SqlExpenseNumberLock(CarRentalDbContext db) : IExpenseNumberLock
{
    private const string Sql = """
        DECLARE @result int;
        EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @timeout;
        SELECT @result;
        """;

    public async Task<Result> AcquireAsync(Guid agencyId, int year, CancellationToken cancellationToken = default)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("The numbering lock of the expenses is owned by a transaction: acquire it inside IUnitOfWork.ExecuteAsync.");

        var wait = IExpenseNumberLock.DefaultTimeout;
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = Sql;
        command.CommandTimeout = (int)Math.Ceiling(wait.TotalSeconds) + 30;
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = string.Create(CultureInfo.InvariantCulture, $"cash:expense-number:{agencyId:N}:{year:D4}") });
        command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = (int)wait.TotalMilliseconds });

        var outcome = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return outcome is 0 or 1 ? Result.Success() : ExpenseErrors.ConcurrentUpdate;
    }
}
