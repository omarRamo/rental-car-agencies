using CarRental.Application.Cash.Contracts;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;

namespace CarRental.Application.Accounting.Reconciliation;

/// <summary>What a difference of a reconciliation is.</summary>
public enum DifferenceKind
{
    /// <summary>A closed cash session counted more or less than expected.</summary>
    CashVariance = 1,

    /// <summary>The cash payments recorded in a session differ from the movements of its drawer.</summary>
    CashReceiptsMismatch = 2,

    /// <summary>An online payment flagged by the gateway checks (unknown status, mismatch, unconfirmed operation…).</summary>
    GatewayAttention = 3,

    /// <summary>An online payment settled in the month is not on the merchant statement.</summary>
    StatementMissing = 4,

    /// <summary>A line of the merchant statement matches no online payment.</summary>
    StatementExtra = 5,

    /// <summary>The statement and the payment do not give the same amount.</summary>
    StatementAmountMismatch = 6,

    /// <summary>The payout of a channel differs from what it should pay.</summary>
    PayoutDifference = 7,

    /// <summary>A channel should pay for the month and no payout is entered.</summary>
    PayoutMissing = 8,
}

/// <summary>A justification of a difference: why, its amount then, who and when.</summary>
public sealed record JustificationView(Guid Id, string Reason, Money Amount, DateTimeOffset JustifiedAtUtc, string? JustifiedByName);

/// <summary>
/// A difference of a reconciliation of a month, with its justifications (the latest first): it is justified when one of
/// them has its current amount (a difference that changed shows again, its former justifications still visible).
/// </summary>
public sealed record DifferenceView(
    string Key,
    ReconciliationArea Area,
    DifferenceKind Kind,
    Money Amount,
    string Subject,
    DateOnly? Date,
    IReadOnlyList<JustificationView> Justifications)
{
    public JustificationView? Current => Justifications.FirstOrDefault(j => j.Amount == Amount);

    public bool IsJustified => Current is not null;
}

/// <summary>A cash session of the month against the cash payments recorded in it.</summary>
public sealed record CashSessionReconciliationView(
    Guid SessionId,
    DateOnly BusinessDate,
    string CashierName,
    CashSessionStatusInfo Status,
    Money ReceivedRecorded,
    Money ReceivedInDrawer,
    Money RefundsRecorded,
    Money RefundsInDrawer,
    Money ExpensesInDrawer,
    Money? RevenueVariance,
    Money? DepositVariance,
    string? CashierJustification);

public sealed record CashReconciliationView(
    Guid AgencyId,
    AccountingMonth Month,
    IReadOnlyList<CashSessionReconciliationView> Sessions,
    IReadOnlyList<DifferenceView> Differences);

/// <summary>How an online payment stands against the merchant statement.</summary>
public enum StatementMatch
{
    Matched = 1,
    AmountMismatch = 2,
    Missing = 3,

    /// <summary>No statement covers the month: only the gateway statuses are compared.</summary>
    NoStatement = 4,
}

/// <summary>An online payment of the month (settled in it), its gateway state and how the statement matches it.</summary>
public sealed record OnlinePaymentReconciliationView(
    Guid PaymentId,
    string? BookingNumber,
    PaymentDirectionInfo Direction,
    PaymentStatusInfo Status,
    Money Amount,
    string OrderNumber,
    string? GatewayOrderId,
    int? GatewayStatusCode,
    GatewayAttentionInfo? Attention,
    DateOnly? SettledOn,
    StatementMatch Match,
    Money? StatementAmount);

/// <summary>A line of a merchant statement of the month and whether it matches a payment.</summary>
public sealed record StatementLineView(
    Guid Id,
    Guid StatementId,
    DateOnly TransactionDate,
    string Reference,
    StatementLineKind Kind,
    Money Amount,
    Money? Fee,
    DateOnly? SettledOn,
    bool IsMatched,
    string? Note);

/// <summary>A merchant statement brought in (file or manual entry).</summary>
public sealed record StatementView(
    Guid Id,
    StatementOrigin Origin,
    string? FileName,
    string Format,
    DateTimeOffset ImportedAtUtc,
    string? ImportedByName,
    int LineCount,
    DateOnly FirstDate,
    DateOnly LastDate,
    bool TouchesClosedMonth);

public sealed record ClicToPayReconciliationView(
    Guid AgencyId,
    AccountingMonth Month,
    bool HasStatement,
    IReadOnlyList<OnlinePaymentReconciliationView> Payments,
    IReadOnlyList<StatementLineView> Lines,
    IReadOnlyList<DifferenceView> Differences)
{
    public int MatchedCount => Payments.Count(p => p.Match == StatementMatch.Matched);

    public int MissingCount => Payments.Count(p => p.Match == StatementMatch.Missing);

    public int ExtraCount => Lines.Count(l => !l.IsMatched);
}

/// <summary>The Booking.com (and other channels) settlements of the month, from the reconciliation of the channels (package 5A).</summary>
public sealed record ChannelsReconciliationView(
    Guid AgencyId,
    AccountingMonth Month,
    IReadOnlyList<ChannelSettlementInfo> Settlements,
    IReadOnlyList<DifferenceView> Differences);

/// <summary>A line typed by hand from the merchant portal (amounts in dinars).</summary>
public sealed record StatementLineForm(DateOnly TransactionDate, string Reference, StatementLineKind Kind, decimal Amount, decimal? Fee, DateOnly? SettledOn, string? Note);

/// <summary>
/// The outcome of an import: the statement recorded, or nothing and the numbers of the lines of the file that could not be
/// read (1 = the header).
/// </summary>
public sealed record StatementImportOutcome(Guid? StatementId, int LineCount, IReadOnlyList<int> InvalidLines, bool HeaderInvalid)
{
    public bool IsImported => StatementId is not null;
}
