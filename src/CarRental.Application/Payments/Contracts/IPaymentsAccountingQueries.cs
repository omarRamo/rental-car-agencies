using CarRental.Domain.Common;

namespace CarRental.Application.Payments.Contracts;

/// <summary>
/// Read API of the Payments module for the pre-accounting and the reconciliations (package 3B): the money that moved over a
/// period — payments received and refunds paid back, deposits taken and settled — and the online payments with what the
/// gateway last reported. Deposits are never payments (invariant 8): they come apart. No permission check (the caller
/// checks its own).
/// </summary>
/// <remarks>A separate interface from <see cref="IPaymentsQueries"/>, so that its implementers are not affected.</remarks>
public interface IPaymentsAccountingQueries
{
    /// <summary>
    /// The payments received and the refunds paid back by an agency whose money moved (captured, paid back) on the Tunis
    /// days <paramref name="from"/> to <paramref name="to"/> (both included), oldest first.
    /// </summary>
    Task<IReadOnlyList<SettledPaymentInfo>> GetSettledPaymentsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// The deposits of an agency taken, or settled (released, retained in part or in full), on the Tunis days
    /// <paramref name="from"/> to <paramref name="to"/>: one line per event, oldest first.
    /// </summary>
    Task<IReadOnlyList<DepositMovementInfo>> GetDepositMovementsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// The online payments (Clic to Pay, incoming and refunds) of an agency recorded or settled on the Tunis days
    /// <paramref name="from"/> to <paramref name="to"/>, with the last status code read from the gateway and the flag of the
    /// back office, oldest first.
    /// </summary>
    Task<IReadOnlyList<OnlinePaymentStateInfo>> GetOnlinePaymentsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>The cash payments and refunds recorded in each of <paramref name="sessionIds"/> (settled ones), for the cash reconciliation.</summary>
    Task<IReadOnlyList<SettledPaymentInfo>> GetCashPaymentsOfSessionsAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// Money received from (<see cref="PaymentDirectionInfo.Incoming"/>) or paid back to (<see cref="PaymentDirectionInfo.Refund"/>)
/// a customer: its means, the amount that moved, the Tunis day and instant, its reference and, for cash, its session.
/// </summary>
public sealed record SettledPaymentInfo(
    Guid PaymentId,
    Guid BookingId,
    PaymentDirectionInfo Direction,
    PaymentMethodInfo Method,
    Money Amount,
    DateOnly Date,
    DateTimeOffset SettledAtUtc,
    string? ExternalReference,
    Guid? OriginalPaymentId,
    Guid? CashSessionId);

/// <summary>What happened to a deposit on a day.</summary>
public enum DepositMovementKindInfo
{
    /// <summary>The deposit was taken: the agency owes it to the customer.</summary>
    Taken = 1,

    /// <summary>The deposit was settled: <see cref="DepositMovementInfo.Retained"/> kept, the rest returned.</summary>
    Settled = 2,
}

/// <summary>A deposit taken or settled: its mode, amount, the part retained and returned at the settlement, the day and instant.</summary>
public sealed record DepositMovementInfo(
    Guid DepositId,
    Guid BookingId,
    DepositModeInfo Mode,
    DepositMovementKindInfo Kind,
    Money Amount,
    Money Retained,
    Money Returned,
    DateOnly Date,
    DateTimeOffset AtUtc,
    string? ExternalReference,
    Guid? CashSessionId);

/// <summary>
/// An online payment and what the gateway reported: our status and amounts (captured, refunded), the order number sent to
/// Clic to Pay (32 hexadecimal characters) and the order id it returned, the last status code read server to server and
/// the flag raised for the back office, if any.
/// </summary>
public sealed record OnlinePaymentStateInfo(
    Guid PaymentId,
    Guid BookingId,
    PaymentDirectionInfo Direction,
    PaymentStatusInfo Status,
    Money Amount,
    Money CapturedAmount,
    Money RefundedAmount,
    string OrderNumber,
    string? GatewayOrderId,
    int? GatewayStatusCode,
    GatewayAttentionInfo? Attention,
    Guid? OriginalPaymentId,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? SettledAtUtc);
