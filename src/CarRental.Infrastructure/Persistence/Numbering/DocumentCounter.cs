using CarRental.Domain.Common;

namespace CarRental.Infrastructure.Persistence.Numbering;

/// <summary>Last number issued per agency, document type and fiscal year (gapless counters, ADR-0016 and ADR-0067).</summary>
public sealed class DocumentCounter
{
    private DocumentCounter()
    {
    }

    public Guid AgencyId { get; private set; }

    public DocumentType DocumentType { get; private set; }

    public int Year { get; private set; }

    public long LastValue { get; private set; }
}
