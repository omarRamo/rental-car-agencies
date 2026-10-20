namespace CarRental.Domain.Cash;

/// <summary>Life cycle of a cash session: open during the day, closed by its cashier with the count, locked by a manager.</summary>
public enum CashSessionStatus
{
    /// <summary>Ouverte: movements are recorded.</summary>
    Open = 1,

    /// <summary>Clôturée: counted by the cashier, variances justified; no more movement.</summary>
    Closed = 2,

    /// <summary>Verrouillée: checked by a manager or the accountant; immutable (corrections go to a later session).</summary>
    Locked = 3,
}

/// <summary>
/// The two pockets of a cash drawer (SPEC §8 "Caisse"): the agency's own cash (opening float, payments, refunds, kept
/// deposits) and the cash deposits, owed to customers and counted apart.
/// </summary>
public enum CashPocket
{
    /// <summary>Revenue cash: money of the agency.</summary>
    Revenue = 1,

    /// <summary>Deposit cash: cash deposits held for customers (a liability, never revenue).</summary>
    Deposit = 2,
}

/// <summary>Type of a cash movement, with its effect on the two pockets.</summary>
public enum CashMovementKind
{
    /// <summary>Cash payment received from a customer: revenue +.</summary>
    PaymentReceived = 1,

    /// <summary>Cash refund paid to a customer: revenue −.</summary>
    RefundPaid = 2,

    /// <summary>Cash deposit received: deposit +.</summary>
    DepositReceived = 3,

    /// <summary>Cash deposit (or its unretained part) given back to the customer: deposit −.</summary>
    DepositReturned = 4,

    /// <summary>Part of a cash deposit kept by the agency: deposit −, revenue +.</summary>
    DepositRetained = 5,

    /// <summary>A cash deposit received in another session (kept in the safe or in another drawer) brought into this drawer to be settled: deposit +.</summary>
    DepositTransferIn = 6,

    /// <summary>A cash deposit of this open session handed over to the drawer of the agent who settles it: deposit −.</summary>
    DepositTransferOut = 7,

    /// <summary>Other cash put in the drawer, with a reason (change from the bank…): revenue +.</summary>
    ManualIn = 8,

    /// <summary>Other cash taken from the drawer, with a reason (bank deposit, small purchase…): revenue −.</summary>
    ManualOut = 9,

    /// <summary>Correction of an error of an earlier (locked) session, signed, on one pocket, with a reason.</summary>
    Adjustment = 10,
}

/// <summary>Direction of a manual movement.</summary>
public enum CashFlowDirection
{
    In = 1,
    Out = 2,
}
