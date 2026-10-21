using CarRental.Domain.Common;

namespace CarRental.Domain.Billing;

// Domain events of the Billing module, written to the outbox with the change (ADR-0013). Identifiers, numbers and
// amounts only: never a buyer name or an e-mail address (personal data stays in the tables).

/// <summary>An invoice was issued (number assigned, content frozen): its PDF, its TTN transmission and its e-mail follow.</summary>
public sealed record InvoiceIssued(
    Guid InvoiceId,
    Guid AgencyId,
    string Number,
    InvoiceKind Kind,
    Guid? BookingId,
    Guid? FineId,
    Money TotalDue,
    EInvoiceMode EInvoiceMode) : IDomainEvent;

/// <summary>A credit note was issued against an invoice (negative amounts).</summary>
public sealed record CreditNoteIssued(
    Guid CreditNoteId,
    Guid InvoiceId,
    Guid AgencyId,
    string Number,
    string InvoiceNumber,
    CreditNoteScope Scope,
    Money TotalInclVat,
    EInvoiceMode EInvoiceMode) : IDomainEvent;

/// <summary>TTN accepted a document and returned its unique reference.</summary>
public sealed record BillingDocumentTransmitted(Guid DocumentId, BillingDocumentType Type, string Number, string Reference) : IDomainEvent;

/// <summary>A transmission to TTN failed (it is retried through the outbox, and listed on the invoices screen).</summary>
public sealed record BillingDocumentTransmissionFailed(Guid DocumentId, BillingDocumentType Type, string Number, int Attempts) : IDomainEvent;

/// <summary>The PDF of a document was stored in the private container <c>invoices</c>.</summary>
public sealed record BillingDocumentPdfStored(Guid DocumentId, BillingDocumentType Type, string Number, string Path) : IDomainEvent;

/// <summary>A document was e-mailed to its buyer (queued in the outbox with its PDF attached).</summary>
public sealed record BillingDocumentEmailed(Guid DocumentId, BillingDocumentType Type, string Number) : IDomainEvent;

/// <summary>A traffic fine was recorded and matched (or not) with the rental that had the vehicle.</summary>
public sealed record FineRecorded(Guid FineId, Guid AgencyId, FineMatch Match, Guid? BookingId) : IDomainEvent;

/// <summary>An agent chose the rental file and the driver of a fine.</summary>
public sealed record FineRentalAssigned(Guid FineId, Guid AgencyId, Guid BookingId) : IDomainEvent;

/// <summary>A fine changed status (designated, re-invoiced, paid, closed).</summary>
public sealed record FineStatusChanged(Guid FineId, Guid AgencyId, FineStatus Previous, FineStatus Status) : IDomainEvent;

/// <summary>A claim was opened for a vehicle.</summary>
public sealed record ClaimOpened(Guid ClaimId, Guid AgencyId, Guid VehicleId, Guid? BookingId) : IDomainEvent;

/// <summary>The details, costs, reimbursements or files of a claim changed.</summary>
public sealed record ClaimUpdated(Guid ClaimId, Guid AgencyId, Guid VehicleId) : IDomainEvent;

/// <summary>A claim changed status.</summary>
public sealed record ClaimStatusChanged(Guid ClaimId, Guid AgencyId, ClaimStatus Previous, ClaimStatus Status) : IDomainEvent;

/// <summary>A file of a claim was removed: its blob is deleted after the commit.</summary>
public sealed record ClaimFileDiscarded(Guid ClaimId, string Path) : IDomainEvent;
