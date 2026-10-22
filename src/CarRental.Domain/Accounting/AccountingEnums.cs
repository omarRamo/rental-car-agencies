namespace CarRental.Domain.Accounting;

/// <summary>
/// The journals of the pre-accounting (SPEC §8 "journaux des ventes, achats, banque et caisse"), plus the miscellaneous
/// operations journal for the movements that are neither sales, purchases, bank nor cash: guarantees of the deposits taken
/// by card imprint or cheque, the deposit retentions applied to an invoice, the payments collected by a sales channel.
/// </summary>
public enum Journal
{
    /// <summary>VT: invoices and credit notes.</summary>
    Sales = 1,

    /// <summary>AC: expenses (supplier invoices).</summary>
    Purchases = 2,

    /// <summary>BQ: card terminal, cheques, transfers, Clic to Pay, expenses paid by bank, Booking.com payouts, Clic to Pay settlements.</summary>
    Bank = 3,

    /// <summary>CA: the movements of the cash sessions and their counted differences.</summary>
    Cash = 4,

    /// <summary>OD: deposits held by card imprint or cheque, deposit retentions, payments collected by a channel.</summary>
    Miscellaneous = 5,
}

/// <summary>Whether a month of an agency still accepts financial facts.</summary>
public enum AccountingPeriodStatus
{
    /// <summary>Ouverte: invoices, payments, expenses and cash movements may still be dated in the month.</summary>
    Open = 1,

    /// <summary>Clôturée: nothing more is dated in the month; its journals are frozen.</summary>
    Closed = 2,
}

/// <summary>The groups of the operation types of the mapping (display order of the mapping screen).</summary>
public enum MappingGroup
{
    Sales = 1,
    Taxes = 2,
    ThirdParties = 3,
    Treasury = 4,
    Expenses = 5,
    Differences = 6,
}

/// <summary>Where a difference listed by the reconciliations comes from.</summary>
public enum ReconciliationArea
{
    /// <summary>Closed cash sessions against the receipts recorded.</summary>
    Cash = 1,

    /// <summary>Clic to Pay payments against the gateway statuses and the merchant statement.</summary>
    ClicToPay = 2,

    /// <summary>Booking.com payouts against the bookings and their commissions (package 5A).</summary>
    BookingCom = 3,
}

/// <summary>What a line of a merchant statement records.</summary>
public enum StatementLineKind
{
    /// <summary>Money received from a customer.</summary>
    Payment = 1,

    /// <summary>Money paid back to a customer.</summary>
    Refund = 2,
}

/// <summary>How a merchant statement entered the platform.</summary>
public enum StatementOrigin
{
    /// <summary>A file imported through a statement importer (generic CSV today, TODO(spec): the merchant statement of SMT).</summary>
    Import = 1,

    /// <summary>A line typed by hand from the merchant portal.</summary>
    Manual = 2,
}

/// <summary>Whether a check of the monthly closing stops it or only needs a confirmation.</summary>
public enum ClosingCheckSeverity
{
    /// <summary>The month cannot be closed until it is solved.</summary>
    Blocking = 1,

    /// <summary>The month can be closed once the accountant confirms having seen it.</summary>
    Confirmable = 2,
}

/// <summary>The checks run before a month is closed.</summary>
public enum ClosingCheckKind
{
    /// <summary>A cash session of the month is still open or closed but not locked.</summary>
    UnlockedCashSession = 1,

    /// <summary>A reconciliation difference of the month has no justification.</summary>
    UnjustifiedDifference = 2,

    /// <summary>A file of the month calls for an invoice that was not issued.</summary>
    FileToInvoice = 3,

    /// <summary>An invoice or credit note of the month is not accepted by Tunisie TradeNet (pending or failed).</summary>
    EInvoiceNotTransmitted = 4,
}

/// <summary>The journal codes printed in the exports.</summary>
public static class JournalCatalog
{
    public static IReadOnlyList<Journal> All { get; } = [Journal.Sales, Journal.Purchases, Journal.Bank, Journal.Cash, Journal.Miscellaneous];

    /// <summary>The usual French code of a journal: VT, AC, BQ, CA, OD.</summary>
    public static string CodeOf(Journal journal) => journal switch
    {
        Journal.Sales => "VT",
        Journal.Purchases => "AC",
        Journal.Bank => "BQ",
        Journal.Cash => "CA",
        Journal.Miscellaneous => "OD",
        _ => throw new ArgumentOutOfRangeException(nameof(journal), journal, "Unknown journal."),
    };

    /// <summary>The severity of a check of the closing: the cash and the differences must be settled, the rest confirmed.</summary>
    public static ClosingCheckSeverity SeverityOf(ClosingCheckKind kind) => kind switch
    {
        ClosingCheckKind.UnlockedCashSession or ClosingCheckKind.UnjustifiedDifference => ClosingCheckSeverity.Blocking,
        _ => ClosingCheckSeverity.Confirmable,
    };
}
