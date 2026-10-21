using CarRental.Domain.Common;

namespace CarRental.Domain.Billing;

/// <summary>Business errors of the Billing module (invoices, credit notes, e-invoicing, fines, claims).</summary>
public static class BillingErrors
{
    public static readonly Error Forbidden = Error.Forbidden("billing.forbidden", "The current user may not perform this billing operation.");

    public static readonly Error LockTimeout = Error.Conflict(
        "billing.lock_timeout",
        "Another operation is issuing a document for the same file or invoice; try again.");

    // Parties.
    public static readonly Error SellerIdentityIncomplete = Error.Validation(
        "billing.seller_identity_incomplete",
        "The legal identity of the agency (legal name, tax identifier, legal address) is incomplete: no invoice can be issued.");

    public static readonly Error BuyerMissing = Error.Validation("billing.buyer.missing", "The file has no payer to invoice.");

    public static readonly Error BuyerNameRequired = Error.Validation("billing.buyer.name_required", "The buyer of an invoice has a name.");

    public static readonly Error BuyerTaxIdentifierRequired = Error.Validation(
        "billing.buyer.tax_identifier_required",
        "A company is invoiced with its tax identifier (matricule fiscal).");

    public static readonly Error BuyerAddressRequired = Error.Validation(
        "billing.buyer.address_required",
        "A company is invoiced at its billing address.");

    public static readonly Error PartyFieldTooLong = Error.Validation("billing.party.field_too_long", "A field of the seller or of the buyer is too long.");

    // Invoices.
    public static readonly Error InvoiceNotFound = Error.NotFound("billing.invoice.not_found", "The invoice does not exist.");

    public static readonly Error InvoiceNoLine = Error.Validation("billing.invoice.no_line", "An invoice has at least one line with an amount.");

    public static readonly Error InvoiceTooManyLines = Error.Validation("billing.invoice.too_many_lines", "The invoice has too many lines.");

    public static readonly Error InvoiceLineInvalid = Error.Validation(
        "billing.invoice.line_invalid",
        "An invoice line is invalid (label, quantity, VAT rate or amounts).");

    public static readonly Error InvoiceNegativeTotal = Error.Validation("billing.invoice.negative_total", "The total of an invoice cannot be negative.");

    public static readonly Error InvoiceAlreadyIssued = Error.Conflict(
        "billing.invoice.already_issued",
        "This file already has its invoice: correct it with a credit note.");

    public static readonly Error NumberMismatch = Error.Validation(
        "billing.number_mismatch",
        "The number does not belong to the document type or to the fiscal year of its issue date.");

    public static readonly Error MentionInvalid = Error.Validation("billing.mention_invalid", "A mention of the document is empty or too long.");

    // Invoicing moments.
    public static readonly Error BookingNotFound = Error.NotFound("billing.booking.not_found", "The rental file does not exist.");

    public static readonly Error BookingNotClosed = Error.Conflict(
        "billing.booking.not_closed",
        "The final invoice of a file is issued once the file is closed (settled).");

    public static readonly Error BookingNotCancelled = Error.Conflict("billing.booking.not_cancelled", "The file is not cancelled.");

    public static readonly Error NoCancellationFee = Error.Validation(
        "billing.booking.no_cancellation_fee",
        "The cancellation of this file is free (or the fee was waived): there is nothing to invoice.");

    // Credit notes.
    public static readonly Error CreditNoteNotFound = Error.NotFound("billing.credit_note.not_found", "The credit note does not exist.");

    public static readonly Error CreditNoteReasonRequired = Error.Validation(
        "billing.credit_note.reason_required",
        "A credit note states its reason (3 to 500 characters).");

    public static readonly Error CreditNoteNoLine = Error.Validation("billing.credit_note.no_line", "A partial credit note credits at least one line.");

    public static readonly Error CreditNoteLineUnknown = Error.Validation(
        "billing.credit_note.line_unknown",
        "A credited line does not belong to the invoice, or is credited twice.");

    public static readonly Error CreditNoteAmountInvalid = Error.Validation(
        "billing.credit_note.amount_invalid",
        "A credited amount is positive, in millimes, and at most what remains of its line.");

    public static readonly Error CreditNoteLineNotCreditable = Error.Validation(
        "billing.credit_note.line_not_creditable",
        "A negative line (discount, franchise cap) is only cancelled by a total credit note.");

    public static readonly Error CreditNoteExceedsInvoice = Error.Conflict(
        "billing.credit_note.exceeds_invoice",
        "The credit notes would exceed what remains creditable on the invoice.");

    public static readonly Error NothingToCredit = Error.Conflict(
        "billing.credit_note.nothing_to_credit",
        "The invoice is already fully credited.");

    // Electronic invoicing and documents.
    public static readonly Error EInvoiceNotConfigured = Error.Failure(
        "billing.einvoice.not_configured",
        "The TTN electronic invoicing is not configured (TEIF schema, signature and transmission not delivered yet).");

    public static readonly Error EInvoiceTransmissionFailed = Error.Failure(
        "billing.einvoice.transmission_failed",
        "The transmission of the document to TTN failed; it will be retried.");

    public static readonly Error EInvoiceNotTtn = Error.Validation(
        "billing.einvoice.not_ttn",
        "The document was issued in the transitional paper regime: there is nothing to transmit.");

    public static readonly Error EInvoiceAlreadyTransmitted = Error.Conflict(
        "billing.einvoice.already_transmitted",
        "The document was already transmitted to TTN.");

    public static readonly Error EInvoiceReferenceInvalid = Error.Validation("billing.einvoice.reference_invalid", "The TTN reference is empty or too long.");

    public static readonly Error EInvoiceNotFailed = Error.Conflict(
        "billing.einvoice.not_failed",
        "Only a document whose transmission failed can be transmitted again.");

    public static readonly Error PdfAlreadyStored = Error.Conflict("billing.pdf.already_stored", "The PDF of the document is already stored.");

    public static readonly Error PdfInvalid = Error.Validation("billing.pdf.invalid", "The reference of the PDF is invalid.");

    public static readonly Error PdfNotReady = Error.NotFound("billing.pdf.not_ready", "The PDF of the document is not stored yet.");

    public static readonly Error AlreadyEmailed = Error.Conflict("billing.email.already_sent", "The document was already e-mailed.");

    public static readonly Error EmailInvalid = Error.Validation("billing.email.invalid", "The e-mail address is invalid.");

    // Fines.
    public static readonly Error FineNotFound = Error.NotFound("billing.fine.not_found", "The fine does not exist.");

    public static readonly Error FineTicketRequired = Error.Validation(
        "billing.fine.ticket_required",
        "The notice number is required (at most 40 characters).");

    public static readonly Error FineAuthorityRequired = Error.Validation(
        "billing.fine.authority_required",
        "The authority that issued the fine is required (at most 120 characters).");

    public static readonly Error FinePlaceRequired = Error.Validation("billing.fine.place_required", "The place of the offence is required (at most 200 characters).");

    public static readonly Error FinePlateRequired = Error.Validation("billing.fine.plate_required", "The plate number is required (at most 20 characters).");

    public static readonly Error FineAmountInvalid = Error.Validation(
        "billing.fine.amount_invalid",
        "The amount of the fine is positive, in millimes, at most 100 000 TND.");

    public static readonly Error FineOffenceInFuture = Error.Validation("billing.fine.offence_in_future", "The offence cannot be in the future.");

    public static readonly Error FineNotesTooLong = Error.Validation("billing.fine.notes_too_long", "The notes are too long (at most 1 000 characters).");

    public static readonly Error FineDuplicate = Error.Conflict("billing.fine.duplicate", "A fine with this notice number from this authority is already recorded.");

    public static readonly Error FineInvalidTransition = Error.Conflict("billing.fine.invalid_transition", "The fine cannot take this step in its current status.");

    public static readonly Error FineNoRental = Error.Conflict(
        "billing.fine.no_rental",
        "The fine is not attached to a rental file and a driver yet.");

    public static readonly Error FineDriverInvalid = Error.Validation("billing.fine.driver_invalid", "The driver is not a driver of the chosen file.");

    public static readonly Error FineRentalNotCovering = Error.Validation(
        "billing.fine.rental_not_covering",
        "The chosen file did not have the vehicle at the instant of the offence.");

    public static readonly Error FineAlreadyReinvoiced = Error.Conflict("billing.fine.already_reinvoiced", "The fine was already re-invoiced.");

    // Claims.
    public static readonly Error ClaimNotFound = Error.NotFound("billing.claim.not_found", "The claim does not exist.");

    public static readonly Error ClaimVehicleRequired = Error.Validation("billing.claim.vehicle_required", "A claim concerns a vehicle of the agency.");

    public static readonly Error ClaimAccidentInFuture = Error.Validation("billing.claim.accident_in_future", "The accident cannot be in the future.");

    public static readonly Error ClaimDescriptionRequired = Error.Validation(
        "billing.claim.description_required",
        "The circumstances of the accident are described (3 to 2 000 characters).");

    public static readonly Error ClaimFieldTooLong = Error.Validation("billing.claim.field_too_long", "A field of the claim is too long.");

    public static readonly Error ClaimAmountInvalid = Error.Validation(
        "billing.claim.amount_invalid",
        "The amount is positive, in millimes, at most 1 000 000 TND.");

    public static readonly Error ClaimLabelRequired = Error.Validation("billing.claim.label_required", "A cost or a reimbursement has a label (at most 200 characters).");

    public static readonly Error ClaimEntryNotFound = Error.NotFound("billing.claim.entry_not_found", "The cost, reimbursement or file does not exist on this claim.");

    public static readonly Error ClaimTooManyEntries = Error.Validation("billing.claim.too_many_entries", "The claim has too many costs, reimbursements or files.");

    public static readonly Error ClaimClosed = Error.Conflict("billing.claim.closed", "A closed claim does not change any more.");

    public static readonly Error ClaimInvalidTransition = Error.Conflict("billing.claim.invalid_transition", "The claim cannot take this step in its current status.");

    public static readonly Error ClaimFileInvalid = Error.Validation(
        "billing.claim.file_invalid",
        "The file must be a JPEG, PNG, WebP image or a PDF of at most 10 MB.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "billing.concurrent_update",
        "The record was changed meanwhile: reload it and try again.");
}
