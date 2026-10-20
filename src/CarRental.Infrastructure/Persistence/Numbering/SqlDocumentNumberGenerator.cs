using System.Data;
using System.Globalization;
using CarRental.Application.Common.Numbering;
using CarRental.Domain.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CarRental.Infrastructure.Persistence.Numbering;

/// <summary>
/// Gapless yearly counters (ADR-0016): one atomic <c>MERGE … WITH (HOLDLOCK)</c> increments (or creates) the row of
/// the document type and fiscal year inside the caller's transaction. The row stays locked until commit, so concurrent
/// transactions wait and a rollback gives the number back.
/// </summary>
internal sealed class SqlDocumentNumberGenerator(CarRentalDbContext db) : IDocumentNumberGenerator
{
    private const string Sql = $"""
        MERGE [{Schemas.Infra}].[DocumentCounters] WITH (HOLDLOCK) AS target
        USING (SELECT @type AS [DocumentType], @year AS [Year]) AS source
            ON target.[DocumentType] = source.[DocumentType] AND target.[Year] = source.[Year]
        WHEN MATCHED THEN
            UPDATE SET target.[LastValue] = target.[LastValue] + 1
        WHEN NOT MATCHED THEN
            INSERT ([DocumentType], [Year], [LastValue]) VALUES (source.[DocumentType], source.[Year], 1)
        OUTPUT inserted.[LastValue];
        """;

    public async Task<DocumentNumber> NextAsync(DocumentType type, int fiscalYear, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown document type.");
        }

        if (fiscalYear is < 2000 or > 9999)
        {
            throw new ArgumentOutOfRangeException(nameof(fiscalYear), fiscalYear, "The fiscal year must have four digits.");
        }

        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "Document numbers are gapless only inside the document's transaction: call NextAsync within IUnitOfWork.ExecuteAsync.");

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = Sql;
        command.Parameters.Add(new SqlParameter("@type", SqlDbType.VarChar, 32) { Value = type.ToString() });
        command.Parameters.Add(new SqlParameter("@year", SqlDbType.Int) { Value = fiscalYear });

        var value = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return DocumentNumber.Create(type, fiscalYear, value).Value;
    }
}
