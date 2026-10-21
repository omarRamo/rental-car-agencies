namespace CarRental.Domain.Billing;

// Values of these enums are stored: never renumber them.

/// <summary>What an invoice bills (docs/conception/phase-3-finance.md §1, "Moments de facturation").</summary>
public enum InvoiceKind
{
    /// <summary>The final invoice of a rental file, issued at its closing: the current quote and the return charges.</summary>
    Rental = 1,

    /// <summary>The cancellation fee recorded on a cancelled file.</summary>
    CancellationFee = 2,

    /// <summary>A traffic fine re-invoiced to the payer of the file, with the management fee of the agency.</summary>
    FineReinvoicing = 3,
}

/// <summary>
/// Accounting nature of an invoice line: what package 3B maps to an account of the chart of accounts (rental revenue,
/// discounts granted, extras, protection, delivery, the daily contribution collected for the State, return fees by
/// kind, damages, cancellation fees, disbursements of fines, management fees).
/// </summary>
public enum InvoiceLineNature
{
    /// <summary>Rental days (season, event, extension) and the guaranteed model supplement.</summary>
    Rental = 1,

    /// <summary>Duration tier, promotion or manual discount (negative lines).</summary>
    Discount = 2,

    /// <summary>Extras chosen by the customer and the automatic supplements (young driver, after hours, one way).</summary>
    Extras = 3,

    /// <summary>Franchise waiver (insurance and protection).</summary>
    Protection = 4,

    /// <summary>Delivery or collection at a location.</summary>
    Delivery = 5,

    /// <summary>Daily contribution of 2.000 TND excluding VAT per day and vehicle (finance law 2026), quote and late days.</summary>
    DailyContribution = 6,

    /// <summary>Return fee: kilometres beyond the included mileage.</summary>
    ExtraMileage = 7,

    /// <summary>Return fee: missing fuel (full to full).</summary>
    Fuel = 8,

    /// <summary>Return fee: late days at the current plan.</summary>
    LateReturn = 9,

    /// <summary>Return fee: cleaning.</summary>
    Cleaning = 10,

    /// <summary>Return fee: other charge entered by an agent.</summary>
    OtherCharge = 11,

    /// <summary>Damage repairs and their franchise cap (negative): an indemnity settled, if so, by the deposit retention.</summary>
    Damage = 12,

    /// <summary>Cancellation fee of the cancellation policy.</summary>
    CancellationFee = 13,

    /// <summary>A traffic fine re-invoiced at its exact amount (disbursement).</summary>
    FineReinvoicing = 14,

    /// <summary>The management fee of a re-invoiced fine.</summary>
    ManagementFee = 15,
}

/// <summary>
/// How the amounts of a line were entered: VAT included (the HT is computed per VAT rate on the group, like the pricing
/// engine) or excluding VAT (the daily contribution: its HT is a fact, its VAT is added).
/// </summary>
public enum AmountBasis
{
    InclVat = 1,
    ExclVat = 2,
}

/// <summary>The buyer of an invoice: a person, or a company identified by its tax identifier.</summary>
public enum BuyerKind
{
    Individual = 1,
    Company = 2,
}

/// <summary>
/// Electronic invoicing regime of a document (PROMPT §9.4): the transitional paper regime while the TTN membership of the
/// agency is pending, or the TTN platform (signed TEIF XML, unique reference returned).
/// </summary>
public enum EInvoiceMode
{
    PaperTransitional = 1,
    Ttn = 2,
}

/// <summary>Where a document stands with Tunisie TradeNet.</summary>
public enum EInvoiceStatus
{
    /// <summary>Paper document of the transitional regime: nothing to transmit.</summary>
    NotRequired = 1,

    /// <summary>To transmit to TTN (TEIF XML, signature).</summary>
    Pending = 2,

    /// <summary>Accepted by TTN, which returned its unique reference.</summary>
    Transmitted = 3,

    /// <summary>The last transmission failed; it is retried through the outbox.</summary>
    Failed = 4,
}

/// <summary>Invoice or credit note (the two numbered billing documents).</summary>
public enum BillingDocumentType
{
    Invoice = 1,
    CreditNote = 2,
}

/// <summary>Whether a credit note cancels everything that remains of the invoice or part of its lines.</summary>
public enum CreditNoteScope
{
    Total = 1,
    Partial = 2,
}

/// <summary>Life of a traffic fine received by the agency.</summary>
public enum FineStatus
{
    /// <summary>Entered from the notice (procès-verbal).</summary>
    Recorded = 1,

    /// <summary>The driver was designated to the authority (designation form).</summary>
    Designated = 2,

    /// <summary>Re-invoiced to the payer of the file.</summary>
    Reinvoiced = 3,

    /// <summary>Paid to the authority.</summary>
    Paid = 4,

    /// <summary>Nothing left to do.</summary>
    Closed = 5,
}

/// <summary>How the rental file of a fine was found.</summary>
public enum FineMatch
{
    /// <summary>Exactly one rental had the vehicle at the instant of the offence.</summary>
    Matched = 1,

    /// <summary>Several rentals cover the instant: an agent chooses.</summary>
    Ambiguous = 2,

    /// <summary>No rental had the vehicle at that instant (the agency's own use, or a wrong notice).</summary>
    NoRental = 3,

    /// <summary>The plate is not a vehicle of the agency.</summary>
    UnknownVehicle = 4,

    /// <summary>An agent chose the file and the driver.</summary>
    Manual = 5,
}

/// <summary>Life of a claim (sinistre).</summary>
public enum ClaimStatus
{
    Open = 1,

    /// <summary>Declared to the insurer, waiting for its decision or its payment.</summary>
    AtInsurer = 2,

    Closed = 3,
}

/// <summary>Kind of a file attached to a claim.</summary>
public enum ClaimDocumentKind
{
    /// <summary>The accident report (constat amiable), photographed or scanned.</summary>
    AccidentReport = 1,

    Photo = 2,

    /// <summary>Expert report, repair invoice, insurer letter…</summary>
    Other = 3,
}
