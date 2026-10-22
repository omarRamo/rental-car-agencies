using CarRental.Domain.Common;

namespace CarRental.Application.Common.Numbering;

/// <summary>
/// Readable document numbers (<c>LOC-YYYY-NNNNNN</c>, <c>FAC-YYYY-NNNNNN</c>, <c>AV-YYYY-NNNNNN</c>) from a counter row
/// per agency, type and fiscal year, incremented inside the caller's transaction (ADR-0016). Each agency is a legal entity
/// with its own series (ADR-0067): two agencies both issue <c>FAC-2026-000001</c>.
/// </summary>
/// <remarks>
/// Gapless by construction: the increment commits or rolls back with the document that uses it, and concurrent
/// transactions wait on the counter row. Must be called inside <see cref="Persistence.IUnitOfWork"/>; calling it without
/// a transaction throws <see cref="InvalidOperationException"/>.
/// </remarks>
public interface IDocumentNumberGenerator
{
    Task<DocumentNumber> NextAsync(Guid agencyId, DocumentType type, int fiscalYear, CancellationToken cancellationToken = default);
}

public static class DocumentNumberGeneratorExtensions
{
    /// <summary>
    /// Next number of <paramref name="agencyId"/> for a document dated <paramref name="documentDate"/>: the fiscal year is
    /// its year in Africa/Tunis.
    /// </summary>
    public static Task<DocumentNumber> NextForDateAsync(
        this IDocumentNumberGenerator generator,
        Guid agencyId,
        DocumentType type,
        DateTimeOffset documentDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return generator.NextAsync(agencyId, type, TunisTime.DateOf(documentDate).Year, cancellationToken);
    }
}
