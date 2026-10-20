using CarRental.Domain.Common;

namespace CarRental.Infrastructure.Persistence.Numbering;

/// <summary>Last number issued per document type and fiscal year (gapless counters, ADR-0016).</summary>
public sealed class DocumentCounter
{
    private DocumentCounter()
    {
    }

    public DocumentType DocumentType { get; private set; }

    public int Year { get; private set; }

    public long LastValue { get; private set; }
}
