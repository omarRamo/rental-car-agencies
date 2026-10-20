namespace CarRental.Domain.Common;

/// <summary>Kinds of documents that carry a readable, per-year number such as <c>FAC-2027-000045</c>.</summary>
public enum DocumentType
{
    /// <summary>Rental file (dossier de location): <c>LOC-YYYY-NNNNNN</c>.</summary>
    Booking = 1,

    /// <summary>Invoice: <c>FAC-YYYY-NNNNNN</c>, gapless per fiscal year (legal requirement).</summary>
    Invoice = 2,

    /// <summary>Credit note (avoir): <c>AV-YYYY-NNNNNN</c>, gapless per fiscal year.</summary>
    CreditNote = 3,
}
