using System.Globalization;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Endpoints;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace CarRental.Web.Components.Admin.Payments;

/// <summary>Display formats of the payments and cash screens (current UI culture; instants shown in Tunis time).</summary>
public static class PaymentsFormat
{
    /// <summary>Means of payment an agent can record at the counter (every one but Clic to Pay).</summary>
    public static IReadOnlyList<PaymentMethodInfo> CounterMethods { get; } =
    [
        PaymentMethodInfo.Cash,
        PaymentMethodInfo.CardTerminal,
        PaymentMethodInfo.Cheque,
        PaymentMethodInfo.BankTransfer,
        PaymentMethodInfo.ChannelPrepaid,
    ];

    /// <summary>Means an agent can pay money back with at the counter.</summary>
    public static IReadOnlyList<PaymentMethodInfo> RefundMethods { get; } =
    [
        PaymentMethodInfo.Cash,
        PaymentMethodInfo.CardTerminal,
        PaymentMethodInfo.Cheque,
        PaymentMethodInfo.BankTransfer,
    ];

    /// <summary>
    /// <paramref name="methods"/> as an agency offers them: cash goes through a cash session, so only an agency with the
    /// cash module on (<paramref name="cashOffered"/>) takes or pays back cash (ADR-0067).
    /// </summary>
    public static IReadOnlyList<PaymentMethodInfo> Offered(IReadOnlyList<PaymentMethodInfo> methods, bool cashOffered)
    {
        ArgumentNullException.ThrowIfNull(methods);
        return cashOffered ? methods : [.. methods.Where(m => m != PaymentMethodInfo.Cash)];
    }

    /// <summary>The modes of a deposit an agency offers: a cash deposit only with the cash module on.</summary>
    public static IReadOnlyList<DepositModeInfo> DepositModes(bool cashOffered) =>
        [.. Enum.GetValues<DepositModeInfo>().Where(m => cashOffered || m != DepositModeInfo.Cash)];

    /// <summary>Options of the dialogs: full width up to <paramref name="maxWidth"/>, closed by the Escape key.</summary>
    public static DialogOptions Dialog(MaxWidth maxWidth = MaxWidth.Small) =>
        new() { MaxWidth = maxWidth, FullWidth = true, CloseOnEscapeKey = true };

    public static string Amount(Money amount) => amount.ToString("N", CultureInfo.CurrentCulture);

    public static string Amount(Money? amount) => amount is { } value ? Amount(value) : "—";

    /// <summary>A signed amount (variance, adjustment): an explicit "+" for positive values.</summary>
    public static string WithSign(Money amount) => amount.IsPositive ? $"+{Amount(amount)}" : Amount(amount);

    public static string WithSign(Money? amount) => amount is { } value ? WithSign(value) : "—";

    public static string Instant(DateTimeOffset instant) => TunisTime.ToTunis(instant).ToString("g", CultureInfo.CurrentCulture);

    public static string Instant(DateTimeOffset? instant) => instant is { } value ? Instant(value) : "—";

    public static string Date(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);

    /// <summary>The first characters of a booking id (bookings are referenced by id until the Bookings contract gives their number).</summary>
    public static string ShortId(Guid id) => id.ToString("N")[..8].ToUpperInvariant();

    public static bool RequiresReference(PaymentMethodInfo method) =>
        method is PaymentMethodInfo.Cheque or PaymentMethodInfo.BankTransfer or PaymentMethodInfo.ChannelPrepaid;

    public static bool CanAwaitConfirmation(PaymentMethodInfo method) => method is PaymentMethodInfo.BankTransfer or PaymentMethodInfo.Cheque;

    public static bool RequiresReference(DepositModeInfo mode) => mode is DepositModeInfo.CardImprint or DepositModeInfo.Cheque;

    /// <summary>Exact decimal amount typed in a numeric field (MudBlazor gives a decimal; null when empty).</summary>
    public static decimal Value(decimal? amount) => amount ?? 0m;

    public static FinanceTone ToneOf(PaymentStatusInfo status) => status switch
    {
        PaymentStatusInfo.Captured => FinanceTone.Success,
        PaymentStatusInfo.Pending or PaymentStatusInfo.PreAuthorized => FinanceTone.Warning,
        PaymentStatusInfo.Failed => FinanceTone.Danger,
        PaymentStatusInfo.Refunded => FinanceTone.Info,
        _ => FinanceTone.Neutral,
    };

    public static FinanceTone ToneOf(DepositStatusInfo status) => status switch
    {
        DepositStatusInfo.Held => FinanceTone.Warning,
        DepositStatusInfo.Released => FinanceTone.Success,
        DepositStatusInfo.PartiallyRetained => FinanceTone.Info,
        _ => FinanceTone.Danger,
    };

    public static FinanceTone ToneOf(CashSessionStatusInfo status) => status switch
    {
        CashSessionStatusInfo.Open => FinanceTone.Success,
        CashSessionStatusInfo.Closed => FinanceTone.Warning,
        _ => FinanceTone.Neutral,
    };

    /// <summary>Tone of a variance: zero is fine, anything else needs attention.</summary>
    public static FinanceTone ToneOf(Money? variance) => variance is { IsZero: false } ? FinanceTone.Danger : FinanceTone.Success;

    /// <summary>Tone of a flag of an online payment: money at stake (status, amount, late payment, refusal) is red.</summary>
    public static FinanceTone ToneOf(GatewayAttentionInfo attention) => attention switch
    {
        GatewayAttentionInfo.UnknownStatus or GatewayAttentionInfo.CaptureNotConfirmed
            or GatewayAttentionInfo.ReversalNotConfirmed or GatewayAttentionInfo.RefundNotConfirmed => FinanceTone.Warning,
        _ => FinanceTone.Danger,
    };

    public static FinanceTone ToneOf(OnlineRefundStateInfo state) => state switch
    {
        OnlineRefundStateInfo.Refunded => FinanceTone.Success,
        OnlineRefundStateInfo.AwaitingApproval or OnlineRefundStateInfo.Sent => FinanceTone.Warning,
        OnlineRefundStateInfo.Failed => FinanceTone.Danger,
        _ => FinanceTone.Neutral,
    };

    public static FinanceTone ToneOf(GatewayCallOutcomeInfo outcome) => outcome switch
    {
        GatewayCallOutcomeInfo.Succeeded => FinanceTone.Success,
        GatewayCallOutcomeInfo.Unavailable => FinanceTone.Warning,
        _ => FinanceTone.Danger,
    };

    /// <summary>The resource key of the kind of an online payment: pre-authorization, one-step payment or payment link.</summary>
    public static string FlowKey(OnlinePaymentInfo payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        return payment.IsPaymentLink ? "Flow.Link" : $"Flow.{payment.Flow}";
    }

    /// <summary>
    /// A payment page address to show or copy: the address of the gateway as it is, a page of the site (simulated
    /// gateway) made absolute under the base address of the site.
    /// </summary>
    public static string AbsoluteUrl(NavigationManager navigation, string url)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(url);
        return ClicToPayEndpoints.IsWebAddress(url) ? url : navigation.ToAbsoluteUri(url.TrimStart('/')).ToString();
    }

    /// <summary>
    /// The last characters of an order number, for titles and links: the random part of the version 7 id (its first
    /// characters are a timestamp, shared by the payments of the same minute).
    /// </summary>
    public static string ShortOrder(string orderNumber)
    {
        ArgumentNullException.ThrowIfNull(orderNumber);
        return orderNumber.Length > 8 ? orderNumber[^8..].ToUpperInvariant() : orderNumber.ToUpperInvariant();
    }
}

/// <summary>Colour family of a <see cref="FinanceBadge"/>.</summary>
public enum FinanceTone
{
    Neutral = 0,
    Info = 1,
    Success = 2,
    Warning = 3,
    Danger = 4,
}
