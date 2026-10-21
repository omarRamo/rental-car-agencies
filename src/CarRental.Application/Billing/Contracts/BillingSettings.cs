using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Billing.Contracts;

/// <summary>
/// Settings of the Billing module, per agency (the VAT rate, the daily contribution and the stamp duty are platform-wide
/// <see cref="WellKnownSettings"/> that an agency may override).
/// </summary>
public static class BillingSettings
{
    /// <summary>
    /// Issue the final invoice when a file is closed, and the invoice of the cancellation fee when a file is cancelled
    /// with a fee (true). Off, the files wait in the "to invoice" list for an accountant.
    /// </summary>
    public static readonly SettingDefinition<bool> IssueAutomatically =
        SettingDefinitions.Boolean("billing.issue_automatically", true);

    /// <summary>
    /// The agency transmits its invoices to Tunisie TradeNet (TEIF XML, signature) once its membership is finalised
    /// (false: transitional paper regime, the PDF carries the mention below).
    /// </summary>
    public static readonly SettingDefinition<bool> TtnEnabled =
        SettingDefinitions.Boolean("billing.einvoice_ttn", false);

    /// <summary>
    /// The mention printed on the paper invoices of the transitional regime. TODO(spec): exact wording to be validated by
    /// the chartered accountant (note générale n° 2/2026, SPEC §8).
    /// </summary>
    public static readonly SettingDefinition<string> TransitionalMention =
        SettingDefinitions.Text(
            "billing.einvoice_transitional_mention",
            "Facture établie sur support papier à titre transitoire : la demande d'adhésion au système de facturation électronique (Tunisie TradeNet) a été déposée (note générale n° 2/2026).",
            maxLength: 500);

    /// <summary>
    /// Further mentions printed on every invoice and credit note (one paragraph; empty by default). TODO(spec): the final
    /// list of the mandatory mentions comes from the chartered accountant.
    /// </summary>
    public static readonly SettingDefinition<string> LegalMentions =
        SettingDefinitions.Text("billing.invoice_mentions", string.Empty, maxLength: 1000);

    /// <summary>Management fee, VAT included, added when a traffic fine is re-invoiced to the payer (0: none).</summary>
    public static readonly SettingDefinition<Money> FineManagementFee =
        SettingDefinitions.Amount("billing.fine_management_fee", Money.Zero);

    public static IReadOnlyList<SettingDefinition> All { get; } =
        [IssueAutomatically, TtnEnabled, TransitionalMention, LegalMentions, FineManagementFee];
}
