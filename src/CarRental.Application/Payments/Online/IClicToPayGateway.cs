using CarRental.Domain.Common;
using CarRental.Domain.Payments;

namespace CarRental.Application.Payments.Online;

/// <summary>
/// Port to the Clic to Pay REST API (SPEC §9, ADR-0038): implemented by <c>ClicToPayClient</c> (Infrastructure, typed
/// HTTP client) or by <see cref="FakeClicToPay"/> (Development, Testing). Every call is written to the call log by the
/// implementation. Failures are <see cref="Result"/> errors: <see cref="PaymentsErrors.GatewayUnavailable"/> (no usable
/// answer: the request may or may not have been processed), <see cref="PaymentsErrors.GatewayRejected"/> (error code of
/// the gateway), <see cref="PaymentsErrors.GatewayInvalidResponse"/>, <see cref="PaymentsErrors.GatewayDisabled"/>.
/// </summary>
/// <remarks>
/// Only <see cref="GetOrderStatusExtendedAsync"/> is ever retried by an implementation (idempotent read). Registrations,
/// deposits, reversals and refunds are sent once; after a failure the caller reads the status by order number before
/// anything else (ADR-0039). Never call the gateway inside a database transaction.
/// </remarks>
public interface IClicToPayGateway
{
    /// <summary>False when online payment is switched off (<c>ClicToPay:Enabled</c>): every call then fails with <see cref="PaymentsErrors.GatewayDisabled"/>.</summary>
    bool IsEnabled { get; }

    /// <summary><c>register.do</c>: one-step payment; returns the order of the gateway and its payment page.</summary>
    Task<Result<GatewayRegisteredOrder>> RegisterAsync(GatewayRegistration registration, CancellationToken cancellationToken = default);

    /// <summary><c>registerPreAuth.do</c>: pre-authorization; returns the order of the gateway and its payment page.</summary>
    Task<Result<GatewayRegisteredOrder>> RegisterPreAuthAsync(GatewayRegistration registration, CancellationToken cancellationToken = default);

    /// <summary><c>deposit.do</c>: captures a pre-authorized order, at most the pre-authorized amount. Success = acknowledged only.</summary>
    Task<Result> DepositAsync(GatewayOrderOperation operation, CancellationToken cancellationToken = default);

    /// <summary><c>reverse.do</c>: cancels a pre-authorization. Success = acknowledged only.</summary>
    Task<Result> ReverseAsync(GatewayOrderReference order, CancellationToken cancellationToken = default);

    /// <summary><c>refund.do</c>: refunds part or all of a captured order. Success = acknowledged only.</summary>
    Task<Result> RefundAsync(GatewayOrderOperation operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>getOrderStatusExtended.do</c>: the server-to-server status, the only source of truth on a payment (invariant
    /// 7), by gateway order id when known, else by order number.
    /// </summary>
    Task<Result<GatewayOrderStatus>> GetOrderStatusExtendedAsync(GatewayOrderReference order, CancellationToken cancellationToken = default);
}

/// <summary>An order to register at the gateway.</summary>
/// <param name="OrderNumber">Our order number (id of the payment, 32 hexadecimal characters): the idempotency key.</param>
/// <param name="Amount">Amount in TND (converted to the minor unit by the client).</param>
/// <param name="Language">Two-letter language of the payment page; the configured default when empty.</param>
/// <param name="Description">Short non-personal description (booking number), TODO(spec) limit.</param>
/// <param name="Validity">Validity of a payment link (sent only with <c>ClicToPay:SendSessionTimeout</c>, TODO(spec)).</param>
public sealed record GatewayRegistration(string OrderNumber, Money Amount, string? Language, string? Description = null, TimeSpan? Validity = null);

/// <summary>An order registered by the gateway: its id and the address of its payment page (<c>formUrl</c>).</summary>
public sealed record GatewayRegisteredOrder(string GatewayOrderId, string FormUrl);

/// <summary>An order of the gateway: our order number and, when known, the gateway order id.</summary>
public sealed record GatewayOrderReference(string OrderNumber, string? GatewayOrderId);

/// <summary>An operation with an amount on a registered order (capture, refund).</summary>
/// <param name="OrderNumber">Our order number (call log).</param>
/// <param name="GatewayOrderId">The gateway order id (required by the API).</param>
/// <param name="Amount">Amount in TND.</param>
/// <param name="RefundId">The online refund concerned (call log).</param>
public sealed record GatewayOrderOperation(string OrderNumber, string GatewayOrderId, Money Amount, Guid? RefundId = null);

/// <summary>State of an order as classified from its status code (SPEC §9; codes beyond 0, 1, 2 from <see cref="ClicToPayOptions"/>).</summary>
public enum GatewayOrderState
{
    /// <summary>0: registered, not paid.</summary>
    Registered = 1,

    /// <summary>1: amount pre-authorized (two-step payment).</summary>
    PreAuthorized = 2,

    /// <summary>2: amount debited.</summary>
    Deposited = 3,

    /// <summary>Configured "reversed" code (TODO(spec)).</summary>
    Reversed = 4,

    /// <summary>Configured "refunded" code (TODO(spec)).</summary>
    Refunded = 5,

    /// <summary>Configured "declined" code (TODO(spec)).</summary>
    Declined = 6,

    /// <summary>Any other or missing code: never taken for a success; the payment stays as it is and is flagged.</summary>
    Unknown = 7,
}

/// <summary>
/// The answer of <c>getOrderStatusExtended.do</c>, parsed defensively: unknown fields are ignored, missing ones are null.
/// No card data is ever kept (the card details of the answer are not read).
/// </summary>
/// <param name="State">State classified from <paramref name="StatusCode"/>.</param>
/// <param name="StatusCode">Raw <c>orderStatus</c>.</param>
/// <param name="OrderNumber">Our order number as echoed by the gateway.</param>
/// <param name="GatewayOrderId">The gateway order id when the read was by id (or echoed).</param>
/// <param name="Amount">Amount of the order (<c>amount</c>).</param>
/// <param name="ApprovedAmount">Pre-authorized amount (<c>paymentAmountInfo.approvedAmount</c>, TODO(spec)).</param>
/// <param name="DepositedAmount">Debited amount (<c>paymentAmountInfo.depositedAmount</c>, TODO(spec)).</param>
/// <param name="RefundedAmount">Refunded amount (<c>paymentAmountInfo.refundedAmount</c>, TODO(spec)).</param>
/// <param name="ActionCode">Outcome code of the card authorization (<c>actionCode</c>).</param>
/// <param name="ActionCodeDescription">Its text (<c>actionCodeDescription</c>), non-sensitive.</param>
public sealed record GatewayOrderStatus(
    GatewayOrderState State,
    int? StatusCode,
    string? OrderNumber = null,
    string? GatewayOrderId = null,
    Money? Amount = null,
    Money? ApprovedAmount = null,
    Money? DepositedAmount = null,
    Money? RefundedAmount = null,
    string? ActionCode = null,
    string? ActionCodeDescription = null);

/// <summary>Writes the call log of the gateway (append only, outside the transaction of the caller).</summary>
public interface IGatewayCallLog
{
    /// <summary>Records one call; a failure to record is logged and never fails the payment flow.</summary>
    Task RecordAsync(GatewayCallLogEntry entry, CancellationToken cancellationToken = default);
}
