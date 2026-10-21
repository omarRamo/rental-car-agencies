using System.Globalization;
using System.Text.Json;
using CarRental.Application.Payments.Online;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;

namespace CarRental.Infrastructure.Payments.ClicToPay;

/// <summary>
/// The wire format of the Clic to Pay REST API as far as SPEC §9 and the BPC documentation describe it (ADR-0038):
/// form-encoded POST fields, amounts in the minor unit, JSON answers read defensively (unknown fields ignored, missing
/// ones null, numbers as JSON numbers or numeric strings, names case-insensitive). Everything beyond SPEC §9 is marked
/// <c>TODO(spec)</c>; card details of an answer (<c>cardAuthInfo</c>…) are never read.
/// </summary>
internal static class ClicToPayProtocol
{
    public const string RegisterPath = "register.do";
    public const string RegisterPreAuthPath = "registerPreAuth.do";
    public const string DepositPath = "deposit.do";
    public const string ReversePath = "reverse.do";
    public const string RefundPath = "refund.do";
    public const string StatusPath = "getOrderStatusExtended.do";

    /// <summary>Success error code of the API ("0"); an absent error code is a success too (registration answers).</summary>
    public const string SuccessCode = "0";

    public static string PathOf(GatewayOperation operation) => operation switch
    {
        GatewayOperation.Register => RegisterPath,
        GatewayOperation.RegisterPreAuth => RegisterPreAuthPath,
        GatewayOperation.Deposit => DepositPath,
        GatewayOperation.Reverse => ReversePath,
        GatewayOperation.Refund => RefundPath,
        GatewayOperation.GetOrderStatusExtended => StatusPath,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown gateway operation."),
    };

    /// <summary>
    /// Amount in the minor unit: TND × <paramref name="multiplier"/> (1000 = millimes, TODO(spec) to confirm). The
    /// amount is already rounded to the millime (AwayFromZero, <see cref="Money"/>); it is never rounded again here: an
    /// amount the multiplier cannot express exactly is refused.
    /// </summary>
    public static Result<long> ToMinorUnits(Money amount, int multiplier)
    {
        if (amount.Currency != Currency.Tnd || !amount.IsPositive || multiplier <= 0)
        {
            return PaymentsErrors.GatewayAmountNotRepresentable;
        }

        var scaled = amount.Amount * multiplier;
        return scaled == decimal.Truncate(scaled) && scaled <= long.MaxValue ? (long)scaled : PaymentsErrors.GatewayAmountNotRepresentable;
    }

    /// <summary>An amount in the minor unit back in TND (rounded to the millime, AwayFromZero).</summary>
    public static Money FromMinorUnits(long minorUnits, int multiplier) => Money.Tnd(minorUnits / (decimal)multiplier);

    /// <summary>Fields of <c>register.do</c> and <c>registerPreAuth.do</c>.</summary>
    public static List<KeyValuePair<string, string>> Registration(ClicToPayOptions options, GatewayRegistration registration, long minorAmount)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registration);
        var fields = Credentials(options);
        fields.Add(new("orderNumber", registration.OrderNumber));
        fields.Add(new("amount", minorAmount.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(options.Currency))
        {
            // TODO(spec): ISO 4217 numeric currency, only when SMT asks for it (the merchant default applies otherwise).
            fields.Add(new("currency", options.Currency.Trim()));
        }

        fields.Add(new("returnUrl", ClicToPayUrls.ReturnUrlFor(options, registration.OrderNumber)));
        fields.Add(new("failUrl", ClicToPayUrls.FailUrlFor(options, registration.OrderNumber)));
        fields.Add(new("language", LanguageOf(registration.Language, options.Language)));
        if (!string.IsNullOrWhiteSpace(registration.Description))
        {
            // TODO(spec): maximum length of the description at Clic to Pay.
            fields.Add(new("description", registration.Description.Trim()));
        }

        if (options.SendSessionTimeout && registration.Validity is { } validity && validity > TimeSpan.Zero)
        {
            // TODO(spec): BPC parameter, sent only once SMT confirms it (link validity of 24 h).
            fields.Add(new("sessionTimeoutSecs", ((long)validity.TotalSeconds).ToString(CultureInfo.InvariantCulture)));
        }

        return fields;
    }

    /// <summary>Fields of <c>deposit.do</c> and <c>refund.do</c>.</summary>
    public static List<KeyValuePair<string, string>> OrderAmount(ClicToPayOptions options, string gatewayOrderId, long minorAmount)
    {
        var fields = Credentials(options);
        fields.Add(new("orderId", gatewayOrderId));
        fields.Add(new("amount", minorAmount.ToString(CultureInfo.InvariantCulture)));
        return fields;
    }

    /// <summary>Fields of <c>reverse.do</c>.</summary>
    public static List<KeyValuePair<string, string>> Order(ClicToPayOptions options, string gatewayOrderId)
    {
        var fields = Credentials(options);
        fields.Add(new("orderId", gatewayOrderId));
        return fields;
    }

    /// <summary>Fields of <c>getOrderStatusExtended.do</c>: by gateway order id when known, else by our order number.</summary>
    public static List<KeyValuePair<string, string>> Status(ClicToPayOptions options, GatewayOrderReference order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var fields = Credentials(options);
        fields.Add(string.IsNullOrWhiteSpace(order.GatewayOrderId) ? new("orderNumber", order.OrderNumber) : new("orderId", order.GatewayOrderId.Trim()));
        fields.Add(new("language", LanguageOf(null, options.Language)));
        return fields;
    }

    /// <summary>The error code and message of an answer; <see langword="null"/> code when absent.</summary>
    public static (string? Code, string? Message) Error(JsonElement root) => (Text(root, "errorCode"), Text(root, "errorMessage"));

    /// <summary>True when the answer carries no error (no error code, or "0").</summary>
    public static bool IsSuccess(string? errorCode) => errorCode is null or SuccessCode;

    /// <summary>The order id and payment page of a registration answer.</summary>
    public static (string? OrderId, string? FormUrl) Registered(JsonElement root) => (Text(root, "orderId"), Text(root, "formUrl"));

    /// <summary>The fields of a status answer that the platform uses (amounts in the minor unit).</summary>
    public static StatusFields ReadStatus(JsonElement root)
    {
        var amounts = Property(root, "paymentAmountInfo") is { ValueKind: JsonValueKind.Object } info ? info : (JsonElement?)null;
        return new StatusFields(
            Integer(root, "orderStatus") is { } status and >= int.MinValue and <= int.MaxValue ? (int)status : null,
            Text(root, "orderNumber"),
            Text(root, "actionCode"),
            Text(root, "actionCodeDescription"),
            Integer(root, "amount"),
            amounts is { } a ? Integer(a, "approvedAmount") : null,
            amounts is { } d ? Integer(d, "depositedAmount") : null,
            amounts is { } r ? Integer(r, "refundedAmount") : null);
    }

    /// <summary>The value of a property (case-insensitive), or <see langword="null"/>.</summary>
    public static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty(name, out var exact))
        {
            return exact;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>A text or number property as text (trimmed), <see langword="null"/> when absent, null or empty.</summary>
    public static string? Text(JsonElement element, string name) => Property(element, name) switch
    {
        { ValueKind: JsonValueKind.String } value => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
        { ValueKind: JsonValueKind.Number } value => value.GetRawText(),
        _ => null,
    };

    /// <summary>An integer property given as a JSON number or a numeric string, <see langword="null"/> otherwise.</summary>
    public static long? Integer(JsonElement element, string name) => Property(element, name) switch
    {
        { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out var number) => number,
        { ValueKind: JsonValueKind.String } value when long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };

    private static List<KeyValuePair<string, string>> Credentials(ClicToPayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return
        [
            new("userName", options.UserName ?? string.Empty),
            new("password", options.Password ?? string.Empty),
        ];
    }

    private static string LanguageOf(string? requested, string fallback) =>
        requested is { Length: 2 } language && language.All(char.IsAsciiLetterLower) ? language : fallback;
}

/// <summary>The fields of a status answer used by the platform; amounts in the minor unit of the gateway.</summary>
internal sealed record StatusFields(
    int? OrderStatus,
    string? OrderNumber,
    string? ActionCode,
    string? ActionCodeDescription,
    long? Amount,
    long? ApprovedAmount,
    long? DepositedAmount,
    long? RefundedAmount);
