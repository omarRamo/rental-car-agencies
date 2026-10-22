using System.Data;
using System.Globalization;
using CarRental.Application.Common.Numbering;
using CarRental.Application.Common.Security;
using CarRental.Domain.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CarRental.Infrastructure.Persistence.Numbering;

/// <summary>
/// Gapless yearly counters (ADR-0016): one atomic <c>MERGE … WITH (HOLDLOCK)</c> increments (or creates) the row of
/// the agency, document type and fiscal year inside the caller's transaction. The row stays locked until commit, so
/// concurrent transactions wait and a rollback gives the number back. An operation that works for one agency only draws
/// the numbers of that agency (ADR-0067).
/// </summary>
internal sealed class SqlDocumentNumberGenerator(CarRentalDbContext db, IUserContext userContext) : IDocumentNumberGenerator
{
    private const string Sql = $"""
        MERGE [{Schemas.Infra}].[DocumentCounters] WITH (HOLDLOCK) AS target
        USING (SELECT @agency AS [AgencyId], @type AS [DocumentType], @year AS [Year]) AS source
            ON target.[AgencyId] = source.[AgencyId] AND target.[DocumentType] = source.[DocumentType] AND target.[Year] = source.[Year]
        WHEN MATCHED THEN
            UPDATE SET target.[LastValue] = target.[LastValue] + 1
        WHEN NOT MATCHED THEN
            INSERT ([AgencyId], [DocumentType], [Year], [LastValue]) VALUES (source.[AgencyId], source.[DocumentType], source.[Year], 1)
        OUTPUT inserted.[LastValue];
        """;

    public async Task<DocumentNumber> NextAsync(Guid agencyId, DocumentType type, int fiscalYear, CancellationToken cancellationToken = default)
    {
        if (agencyId == Guid.Empty)
        {
            throw new ArgumentException("A document number belongs to an agency.", nameof(agencyId));
        }

        if (!userContext.IsAcrossAgencies && userContext.AgencyId != agencyId)
        {
            throw new InvalidOperationException("An operation that works for one agency only draws the numbers of that agency.");
        }

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
        command.Parameters.Add(new SqlParameter("@agency", SqlDbType.UniqueIdentifier) { Value = agencyId });
        command.Parameters.Add(new SqlParameter("@type", SqlDbType.VarChar, 32) { Value = type.ToString() });
        command.Parameters.Add(new SqlParameter("@year", SqlDbType.Int) { Value = fiscalYear });

        var value = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return DocumentNumber.Create(type, fiscalYear, value).Value;
    }
}
