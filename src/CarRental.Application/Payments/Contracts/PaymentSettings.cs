using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Payments.Contracts;

/// <summary>
/// Business settings owned by the Payments module (SPEC §9), declared to the Referential catalogue by
/// <c>AddPaymentsModule()</c>; read per agency, then platform, then these defaults.
/// </summary>
public static class PaymentSettings
{
    /// <summary>
    /// Online refunds of a payment above this total (refunds already made and in progress included) wait for the
    /// approval of a manager (SPEC §9 « validation du gérant au-delà d'un seuil »): 100.000 TND by default.
    /// </summary>
    public static readonly SettingDefinition<Money> OnlineRefundApprovalThreshold =
        SettingDefinitions.Amount("payments.online_refund_approval_threshold", Money.Tnd(100m));

    /// <summary>Validity of a payment link (fallback mode, phone, balance by link): 24 h (SPEC §9).</summary>
    public static readonly SettingDefinition<TimeSpan> PaymentLinkValidity =
        SettingDefinitions.Duration("payments.payment_link_validity", TimeSpan.FromHours(24));

    /// <summary>
    /// How long an online payment started in the browser (submission, balance) can be completed before it is closed
    /// as expired when the gateway confirms it unpaid: 1 h.
    /// </summary>
    public static readonly SettingDefinition<TimeSpan> OnlineAttemptValidity =
        SettingDefinitions.Duration("payments.online_attempt_validity", TimeSpan.FromHours(1));

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        OnlineRefundApprovalThreshold,
        PaymentLinkValidity,
        OnlineAttemptValidity,
    ];
}
