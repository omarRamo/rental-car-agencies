using CarRental.Domain.Common;

namespace CarRental.Domain.Payments;

/// <summary>
/// One call to the payment gateway (append only): operation, order number, gateway order id, amount, HTTP status,
/// gateway error and status codes, duration and time. It never holds credentials, card data or a raw answer of the
/// gateway (PROMPT §9.1): only these structured, non-sensitive fields, cut to their stored length.
/// </summary>
/// <remarks>
/// A record of the agency of the online payment it concerns, or of the platform (ADR-0067): the client of the platform's
/// single merchant account logs every call on its own, outside the business operation, so a call may concern an order
/// that is not (or not yet) a stored payment; such a call belongs to the platform and only the support reads it.
/// </remarks>
public sealed class GatewayCallLog : Entity, IAgencyOrPlatform
{
    public const int OrderNumberMaxLength = 32;
    public const int GatewayOrderIdMaxLength = 100;
    public const int ErrorCodeMaxLength = 32;
    public const int ErrorMessageMaxLength = 200;
    public const int ActionCodeMaxLength = 32;
    public const int CorrelationIdMaxLength = 64;

    private GatewayCallLog()
    {
    }

    private GatewayCallLog(Guid id)
        : base(id)
    {
    }

    /// <summary>The agency of the online payment of the order, <see langword="null"/> when no stored payment matches it.</summary>
    public Guid? AgencyId { get; private set; }

    public GatewayOperation Operation { get; private set; }

    /// <summary>Our order number: the id of the online payment in 32 hexadecimal characters.</summary>
    public string OrderNumber { get; private set; } = string.Empty;

    /// <summary>The online payment the order belongs to (the order number read as an id).</summary>
    public Guid? PaymentId { get; private set; }

    /// <summary>The online refund concerned by a <c>refund.do</c> call.</summary>
    public Guid? RefundId { get; private set; }

    public string? GatewayOrderId { get; private set; }

    /// <summary>Amount sent (registration, capture, refund).</summary>
    public Money? Amount { get; private set; }

    public GatewayCallOutcome Outcome { get; private set; }

    public int? HttpStatus { get; private set; }

    public string? GatewayErrorCode { get; private set; }

    /// <summary>Error text of the gateway (never echoed to customers), cut to <see cref="ErrorMessageMaxLength"/>.</summary>
    public string? GatewayErrorMessage { get; private set; }

    /// <summary>Order status code of a <c>getOrderStatusExtended.do</c> answer.</summary>
    public int? OrderStatus { get; private set; }

    /// <summary>Action code of a <c>getOrderStatusExtended.do</c> answer (outcome of the card authorization).</summary>
    public string? ActionCode { get; private set; }

    public int DurationMilliseconds { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>
    /// Records a call for <paramref name="agencyId"/>, the agency of the payment of its order (<see langword="null"/>: a
    /// record of the platform). Texts are trimmed and cut to their stored length; a negative duration counts as zero.
    /// </summary>
    public static GatewayCallLog Record(GatewayCallLogEntry entry, Guid? agencyId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (agencyId == Guid.Empty)
        {
            throw new ArgumentException("An agency id cannot be empty.", nameof(agencyId));
        }

        if (!Enum.IsDefined(entry.Operation))
        {
            throw new ArgumentOutOfRangeException(nameof(entry), entry.Operation, "Unknown gateway operation.");
        }

        if (!Enum.IsDefined(entry.Outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(entry), entry.Outcome, "Unknown gateway call outcome.");
        }

        var orderNumber = PaymentText.Truncate(entry.OrderNumber, OrderNumberMaxLength) ?? string.Empty;
        return new GatewayCallLog(NewId())
        {
            AgencyId = agencyId,
            Operation = entry.Operation,
            OrderNumber = orderNumber,
            PaymentId = PaymentIdOf(orderNumber),
            RefundId = entry.RefundId,
            GatewayOrderId = PaymentText.Truncate(entry.GatewayOrderId, GatewayOrderIdMaxLength),
            Amount = entry.Amount,
            Outcome = entry.Outcome,
            HttpStatus = entry.HttpStatus,
            GatewayErrorCode = PaymentText.Truncate(entry.GatewayErrorCode, ErrorCodeMaxLength),
            GatewayErrorMessage = PaymentText.Truncate(entry.GatewayErrorMessage, ErrorMessageMaxLength),
            OrderStatus = entry.OrderStatus,
            ActionCode = PaymentText.Truncate(entry.ActionCode, ActionCodeMaxLength),
            DurationMilliseconds = (int)Math.Clamp(entry.Duration.TotalMilliseconds, 0, int.MaxValue),
            StartedAtUtc = entry.StartedAtUtc,
            CorrelationId = PaymentText.Truncate(entry.CorrelationId, CorrelationIdMaxLength),
        };
    }

    /// <summary>The online payment an order number designates: our order number is its id in 32 hexadecimal characters.</summary>
    public static Guid? PaymentIdOf(string? orderNumber) =>
        Guid.TryParseExact(orderNumber?.Trim(), "N", out var paymentId) ? paymentId : null;
}

/// <summary>What is known of a call to the payment gateway (see <see cref="GatewayCallLog"/>).</summary>
public sealed record GatewayCallLogEntry(
    GatewayOperation Operation,
    string OrderNumber,
    GatewayCallOutcome Outcome,
    DateTimeOffset StartedAtUtc,
    TimeSpan Duration,
    string? GatewayOrderId = null,
    Money? Amount = null,
    int? HttpStatus = null,
    string? GatewayErrorCode = null,
    string? GatewayErrorMessage = null,
    int? OrderStatus = null,
    string? ActionCode = null,
    Guid? RefundId = null,
    string? CorrelationId = null);
