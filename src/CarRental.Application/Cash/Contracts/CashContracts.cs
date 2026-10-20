using CarRental.Application.Common.Paging;
using CarRental.Domain.Common;

namespace CarRental.Application.Cash.Contracts;

/// <summary>Status of a cash session (same values as the domain enum).</summary>
public enum CashSessionStatusInfo
{
    Open = 1,
    Closed = 2,
    Locked = 3,
}

/// <summary>Pocket of a cash drawer: the agency's revenue cash, or the cash deposits owed to customers.</summary>
public enum CashPocketInfo
{
    Revenue = 1,
    Deposit = 2,
}

/// <summary>Direction of a manual movement.</summary>
public enum CashFlowDirectionInfo
{
    In = 1,
    Out = 2,
}

/// <summary>Type of a cash movement (same values as the domain enum).</summary>
public enum CashMovementKindInfo
{
    PaymentReceived = 1,
    RefundPaid = 2,
    DepositReceived = 3,
    DepositReturned = 4,
    DepositRetained = 5,
    DepositTransferIn = 6,
    DepositTransferOut = 7,
    ManualIn = 8,
    ManualOut = 9,
    Adjustment = 10,
}

/// <summary>Opens the drawer of the acting user in an agency with its opening float (fonds de caisse, TND).</summary>
public sealed record OpenCashSessionRequest(Guid AgencyId, decimal OpeningFloat);

/// <summary>Other cash put in or taken from the revenue cash (TND), with a mandatory reason.</summary>
public sealed record ManualCashMovementRequest(CashFlowDirectionInfo Direction, decimal Amount, string Reason);

/// <summary>
/// Correction of an error of an earlier session by a signed amount (TND) on one pocket, with a mandatory reason and the
/// corrected session (closed or locked) when known.
/// </summary>
public sealed record CashAdjustmentRequest(CashPocketInfo Pocket, decimal Amount, string Reason, Guid? CorrectedSessionId = null);

/// <summary>The closing count (TND): revenue cash and deposit cash in the drawer; a justification when they differ from the expected cash.</summary>
public sealed record CloseCashSessionRequest(decimal CountedRevenueCash, decimal CountedDepositCash, string? VarianceJustification = null);

/// <summary>Variances of a closed session: counted minus expected cash (negative: missing cash).</summary>
public sealed record CashSessionClosing(Money RevenueVariance, Money DepositVariance);

/// <summary>Total of each kind of movement of a session (amounts are positive; adjustments are signed).</summary>
public sealed record CashMovementTotals(
    Money PaymentsReceived,
    Money RefundsPaid,
    Money DepositsReceived,
    Money DepositsReturned,
    Money DepositsRetained,
    Money DepositTransfersIn,
    Money DepositTransfersOut,
    Money ManualIn,
    Money ManualOut,
    Money RevenueAdjustments,
    Money DepositAdjustments);

/// <summary>A movement of a session with the expected cash of each pocket right after it (running balances).</summary>
public sealed record CashMovementInfo(
    Guid Id,
    int Sequence,
    CashMovementKindInfo Kind,
    Money Amount,
    Money RevenueEffect,
    Money DepositEffect,
    Money RevenueBalance,
    Money DepositBalance,
    string? Reason,
    Guid? BookingId,
    Guid? PaymentId,
    Guid? DepositId,
    Guid? RelatedSessionId,
    DateTimeOffset RecordedAtUtc,
    string? RecordedByName);

/// <summary>A cash session with its movements in order.</summary>
public sealed record CashSessionDetails(
    Guid Id,
    Guid AgencyId,
    Guid CashierId,
    string CashierName,
    DateOnly BusinessDate,
    CashSessionStatusInfo Status,
    Money OpeningFloat,
    Money ExpectedRevenueCash,
    Money ExpectedDepositCash,
    Money? CountedRevenueCash,
    Money? CountedDepositCash,
    Money? RevenueVariance,
    Money? DepositVariance,
    string? VarianceJustification,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    string? ClosedByName,
    DateTimeOffset? LockedAtUtc,
    string? LockedByName,
    CashMovementTotals Totals,
    IReadOnlyList<CashMovementInfo> Movements)
{
    public bool IsOpen => Status == CashSessionStatusInfo.Open;
}

/// <summary>A cash session in a list (without its movements).</summary>
public sealed record CashSessionSummary(
    Guid Id,
    Guid AgencyId,
    Guid CashierId,
    string CashierName,
    DateOnly BusinessDate,
    CashSessionStatusInfo Status,
    Money OpeningFloat,
    Money ExpectedRevenueCash,
    Money ExpectedDepositCash,
    Money? CountedRevenueCash,
    Money? CountedDepositCash,
    Money? RevenueVariance,
    Money? DepositVariance,
    string? VarianceJustification,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    DateTimeOffset? LockedAtUtc,
    string? LockedByName);

/// <summary>Filter of the session list: business days between <c>From</c> and <c>To</c> (included), cashier, agency, status.</summary>
public sealed record CashSessionFilter(
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? CashierId = null,
    Guid? AgencyId = null,
    CashSessionStatusInfo? Status = null);

/// <summary>Totals of the listed sessions (all pages). Counted amounts and variances only add up the closed and locked sessions.</summary>
public sealed record CashSessionListTotals(
    int Count,
    Money OpeningFloat,
    Money ExpectedRevenueCash,
    Money ExpectedDepositCash,
    Money CountedRevenueCash,
    Money CountedDepositCash,
    Money RevenueVariance,
    Money DepositVariance);

public sealed record CashSessionList(PagedResult<CashSessionSummary> Page, CashSessionListTotals Totals);

/// <summary>A user who has (had) a cash session.</summary>
public sealed record CashierInfo(Guid CashierId, string Name);

/// <summary>A session with the totals of its movements, for the reconciliation of the cash with the payments (Phase 3).</summary>
public sealed record CashSessionTotalsInfo(
    Guid SessionId,
    Guid AgencyId,
    Guid CashierId,
    string CashierName,
    DateOnly BusinessDate,
    CashSessionStatusInfo Status,
    Money OpeningFloat,
    CashMovementTotals Totals,
    Money ExpectedRevenueCash,
    Money ExpectedDepositCash,
    Money? CountedRevenueCash,
    Money? CountedDepositCash,
    Money? RevenueVariance,
    Money? DepositVariance);

/// <summary>A cash payment received or a cash refund paid in the session <c>SessionId</c>.</summary>
public sealed record CashPaymentEntry(Guid SessionId, Guid PaymentId, Guid BookingId, Money Amount);

/// <summary>A cash deposit received in the session <c>SessionId</c>.</summary>
public sealed record CashDepositEntry(Guid SessionId, Guid DepositId, Guid BookingId, Money Amount);

/// <summary>
/// A cash deposit settled from the session <c>SessionId</c>: <c>Amount</c> is the whole deposit, <c>Retained</c> the part
/// kept by the agency (zero for a release), <c>ReceivedInSessionId</c> the session in which it was received.
/// </summary>
public sealed record CashDepositSettlementEntry(Guid SessionId, Guid DepositId, Guid BookingId, Money Amount, Money Retained, Guid ReceivedInSessionId);
