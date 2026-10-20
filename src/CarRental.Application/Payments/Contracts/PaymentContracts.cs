using CarRental.Domain.Common;

namespace CarRental.Application.Payments.Contracts;

/// <summary>Whether money comes from the customer or goes back to the customer (same values as the domain enum).</summary>
public enum PaymentDirectionInfo
{
    Incoming = 1,
    Refund = 2,
}

/// <summary>Means of payment (same values as the domain enum).</summary>
public enum PaymentMethodInfo
{
    /// <summary>Espèces: recorded in the open cash session of the agent.</summary>
    Cash = 1,

    /// <summary>Counter card terminal (TPE).</summary>
    CardTerminal = 2,

    Cheque = 3,

    BankTransfer = 4,

    /// <summary>Prepaid to a sales channel such as Booking.com.</summary>
    ChannelPrepaid = 5,

    /// <summary>Clic to Pay online payment (Phase 2): never recorded at the counter.</summary>
    ClicToPay = 6,
}

/// <summary>Status of a payment (same values as the domain enum). For a refund, <see cref="Captured"/> means "paid back".</summary>
public enum PaymentStatusInfo
{
    Pending = 1,
    PreAuthorized = 2,
    Captured = 3,
    Cancelled = 4,
    Refunded = 5,
    Failed = 6,
}

/// <summary>How a security deposit is held (same values as the domain enum).</summary>
public enum DepositModeInfo
{
    /// <summary>Card imprint on the counter terminal (empreinte TPE).</summary>
    CardImprint = 1,

    Cash = 2,

    Cheque = 3,
}

/// <summary>Status of a security deposit (same values as the domain enum).</summary>
public enum DepositStatusInfo
{
    Held = 1,
    Released = 2,
    PartiallyRetained = 3,
    Retained = 4,
}

/// <summary>Money received at the counter for a booking.</summary>
/// <param name="BookingId">The booking paid (id only: the Payments module does not read bookings).</param>
/// <param name="AgencyId">The agency where the money is received; for cash, the agency of the open cash session of the agent.</param>
/// <param name="Method">Any counter means (every one but Clic to Pay).</param>
/// <param name="Amount">Amount in TND, millimes at most.</param>
/// <param name="ExternalReference">Cheque number, transfer reference or channel booking reference (mandatory for those), terminal receipt (optional).</param>
/// <param name="Notes">Free notes.</param>
/// <param name="AwaitingConfirmation">Bank transfer or cheque recorded before the money is confirmed: pending until confirmed or cancelled.</param>
public sealed record CounterPaymentRequest(
    Guid BookingId,
    Guid AgencyId,
    PaymentMethodInfo Method,
    decimal Amount,
    string? ExternalReference = null,
    string? Notes = null,
    bool AwaitingConfirmation = false);

/// <summary>Money paid back at the counter on a captured incoming payment.</summary>
/// <param name="PaymentId">The incoming payment refunded.</param>
/// <param name="Method">Cash, card terminal, cheque or bank transfer.</param>
/// <param name="Amount">Amount in TND, at most what remains refundable.</param>
/// <param name="Reason">Why the money is paid back (mandatory).</param>
/// <param name="ExternalReference">Cheque number or transfer reference (mandatory for those), terminal receipt (optional).</param>
public sealed record CounterRefundRequest(
    Guid PaymentId,
    PaymentMethodInfo Method,
    decimal Amount,
    string Reason,
    string? ExternalReference = null);

/// <summary>A security deposit taken for a booking.</summary>
/// <param name="BookingId">The booking (id only).</param>
/// <param name="AgencyId">The agency; for cash, the agency of the open cash session of the agent.</param>
/// <param name="Mode">Card imprint, cash or cheque.</param>
/// <param name="Amount">Amount in TND, millimes at most.</param>
/// <param name="ExternalReference">Authorization number of the card imprint or cheque number (mandatory for both).</param>
/// <param name="Notes">Free notes.</param>
public sealed record DepositRequest(
    Guid BookingId,
    Guid AgencyId,
    DepositModeInfo Mode,
    decimal Amount,
    string? ExternalReference = null,
    string? Notes = null);

/// <summary>Part of a deposit kept by the agency (the rest is released).</summary>
/// <param name="Amount">Retained amount in TND, positive and at most the deposit.</param>
/// <param name="Justification">Why (damages, missing fuel, late return…): mandatory.</param>
/// <param name="AttachmentReference">Optional reference to a supporting document (photo, quote, invoice).</param>
public sealed record DepositRetentionRequest(decimal Amount, string Justification, string? AttachmentReference = null);

/// <summary>A payment or a refund.</summary>
public sealed record PaymentInfo(
    Guid Id,
    Guid AgencyId,
    Guid BookingId,
    PaymentDirectionInfo Direction,
    PaymentMethodInfo Method,
    PaymentStatusInfo Status,
    Money Amount,
    Money CapturedAmount,
    Money RefundedAmount,
    Money RefundableAmount,
    Guid? OriginalPaymentId,
    string? ExternalReference,
    string? Notes,
    Guid? CashSessionId,
    DateTimeOffset RecordedAtUtc,
    string? RecordedByName,
    DateTimeOffset? CapturedAtUtc,
    DateTimeOffset StatusChangedAtUtc,
    string? StatusChangedByName,
    string? StatusReason)
{
    public bool IsRefund => Direction == PaymentDirectionInfo.Refund;
}

/// <summary>A security deposit: a liability to the customer while held (invariant 8).</summary>
public sealed record DepositInfo(
    Guid Id,
    Guid AgencyId,
    Guid BookingId,
    DepositModeInfo Mode,
    DepositStatusInfo Status,
    Money Amount,
    Money RetainedAmount,
    Money ReturnedAmount,
    Money OpenLiability,
    string? ExternalReference,
    string? Notes,
    string? RetentionJustification,
    string? AttachmentReference,
    Guid? TakenInCashSessionId,
    Guid? SettledInCashSessionId,
    DateTimeOffset TakenAtUtc,
    string? TakenByName,
    DateTimeOffset? SettledAtUtc,
    string? SettledByName)
{
    public bool IsHeld => Status == DepositStatusInfo.Held;
}

/// <summary>
/// Totals of payments. Deposits never count here (invariant 8). <see cref="Captured"/>: money received (incoming payments
/// captured, including those refunded since); <see cref="Refunded"/>: money paid back; <see cref="NetPaid"/>: captured
/// minus refunded, what the customer actually paid; <see cref="Pending"/> and <see cref="PreAuthorized"/>: not received yet.
/// </summary>
public sealed record PaymentTotals(Money Captured, Money Refunded, Money NetPaid, Money Pending, Money PreAuthorized)
{
    public static PaymentTotals Zero { get; } = new(Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero);
}

/// <summary>
/// Totals of deposits: <see cref="OpenLiability"/> is still owed to customers (held deposits), <see cref="Retained"/> was
/// kept by the agency, <see cref="Returned"/> was given back.
/// </summary>
public sealed record DepositTotals(Money Taken, Money OpenLiability, Money Retained, Money Returned, int HeldCount)
{
    public static DepositTotals Zero { get; } = new(Money.Zero, Money.Zero, Money.Zero, Money.Zero, 0);
}

/// <summary>The payments and refunds of a booking, oldest first, with their totals.</summary>
public sealed record BookingPayments(Guid BookingId, IReadOnlyList<PaymentInfo> Payments, PaymentTotals Totals);

/// <summary>The deposits of a booking, oldest first, with what is still owed back.</summary>
public sealed record BookingDeposits(Guid BookingId, IReadOnlyList<DepositInfo> Deposits, DepositTotals Totals);

/// <summary>Money situation of a booking for the other modules (balance at departure, closing of the file).</summary>
public sealed record BookingPaymentSummary(Guid BookingId, PaymentTotals Payments, DepositTotals Deposits);

/// <summary>Payments settled on a Tunis day with one means of payment (reconciliations, Phase 3).</summary>
public sealed record DailyPaymentTotal(DateOnly Date, PaymentMethodInfo Method, Money Received, Money Refunded, int PaymentCount, int RefundCount)
{
    public Money Net => Received - Refunded;
}

/// <summary>Deposits still owed to customers (a liability, never revenue).</summary>
public sealed record DepositLiabilities(Money Total, int Count, IReadOnlyList<DepositLiabilityByMode> ByMode);

public sealed record DepositLiabilityByMode(DepositModeInfo Mode, Money Total, int Count);
