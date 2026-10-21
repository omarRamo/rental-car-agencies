using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CarRental.Infrastructure.Persistence;

/// <summary>SQL Server options shared by the application and the design-time factory (migrations, EF bundle).</summary>
public static class SqlServerSetup
{
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// Migrations in this assembly, history table in <c>infra</c>, the retrying execution strategy for Azure SQL
    /// transient faults (every user transaction must therefore run through <c>IUnitOfWork</c>, i.e.
    /// <c>CreateExecutionStrategy().ExecuteAsync</c>), and split queries by default.
    /// </summary>
    /// <remarks>
    /// Split queries: an aggregate loaded with several collections (a person with its documents, consents and blacklist
    /// entries) is read with one statement per collection instead of one join whose rows multiply (cartesian explosion).
    /// The statements are not one snapshot: aggregates are protected by their row version (a writer touches the root on
    /// every change of its children), and a query that pages (<c>Skip</c>/<c>Take</c>) over an aggregate with
    /// collections must order by a unique key. <c>AsSingleQuery()</c> remains available for a query that needs one
    /// statement (ADR-0028).
    /// </remarks>
    public static void Configure(SqlServerDbContextOptionsBuilder sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        sql.MigrationsAssembly(typeof(SqlServerSetup).Assembly.GetName().Name);
        sql.MigrationsHistoryTable(MigrationsHistoryTable, Schemas.Infra);
        sql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(15), errorNumbersToAdd: null);
        sql.CommandTimeout(60);
        sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
    }
}
