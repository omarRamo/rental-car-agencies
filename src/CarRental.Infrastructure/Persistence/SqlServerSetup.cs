using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CarRental.Infrastructure.Persistence;

/// <summary>SQL Server options shared by the application and the design-time factory (migrations, EF bundle).</summary>
public static class SqlServerSetup
{
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// Migrations in this assembly, history table in <c>infra</c>, and the retrying execution strategy for Azure SQL
    /// transient faults: every user transaction must therefore run through <c>IUnitOfWork</c>
    /// (<c>CreateExecutionStrategy().ExecuteAsync</c>).
    /// </summary>
    public static void Configure(SqlServerDbContextOptionsBuilder sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        sql.MigrationsAssembly(typeof(SqlServerSetup).Assembly.GetName().Name);
        sql.MigrationsHistoryTable(MigrationsHistoryTable, Schemas.Infra);
        sql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(15), errorNumbersToAdd: null);
        sql.CommandTimeout(60);
    }
}
