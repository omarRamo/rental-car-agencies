using System.Globalization;
using CarRental.Application.Billing.Documents;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Web.Components.Admin.Handovers;
using CarRental.Web.Components.Admin.Pricing;
using CarRental.Web.Resources.Admin.Billing;
using CarRental.Web.Resources.Admin.Bookings;
using CarRental.Web.Resources.Admin.Handovers;
using CarRental.Web.Resources.Admin.Pricing;
using Microsoft.Extensions.Localization;

namespace CarRental.Web.Components.Admin.Billing;

/// <summary>
/// <see cref="IBillingTexts"/> from the resources of the back office, in a given language: the labels of the quote lines
/// (pricing keys, <c>BookingQuote.*</c>), of the return charges (<c>ReturnCharge.Label.*</c> with their detail and the
/// agent's description) and of the billing lines (<c>Billing.*</c>) — the texts frozen on the invoices.
/// </summary>
internal sealed class BillingTexts(
    IStringLocalizer<QuoteResources> quotes,
    IStringLocalizer<BookingsResources> bookings,
    IStringLocalizer<HandoversResources> handovers,
    IStringLocalizer<BillingResources> billing) : IBillingTexts
{
    private const string ReturnChargePrefix = "ReturnCharge.Label.";

    public string LineLabel(string labelKey, IReadOnlyList<string> arguments, string? detail, string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelKey);
        ArgumentNullException.ThrowIfNull(arguments);
        return InCulture(language, culture =>
        {
            if (labelKey.StartsWith(ReturnChargePrefix, StringComparison.Ordinal)
                && Enum.TryParse<ReturnChargeKind>(labelKey[ReturnChargePrefix.Length..], out var kind))
            {
                return HandoverText.Charge(key => handovers[key].Value, kind, arguments, detail, culture);
            }

            IStringLocalizer localizer = labelKey.StartsWith("Billing.", StringComparison.Ordinal)
                ? billing
                : BookingQuoteLabels.IsBookingLabel(labelKey) ? bookings : quotes;
            var label = PricingDisplay.QuoteLabel(localizer, labelKey, arguments);
            return string.IsNullOrWhiteSpace(detail) ? label : $"{label} — {detail}";
        });
    }

    public string Text(string key, string language, params object[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return InCulture(language, _ =>
        {
            var text = arguments.Length == 0 ? billing[key] : billing[key, arguments];
            return text.ResourceNotFound ? key : text.Value;
        });
    }

    /// <summary>The culture of a document language (<c>en</c>, otherwise French).</summary>
    public static CultureInfo CultureOf(string? language) =>
        CultureInfo.GetCultureInfo(string.Equals(language, "en", StringComparison.Ordinal) ? "en" : "fr");

    /// <summary>Runs <paramref name="text"/> with the current (UI) culture of <paramref name="language"/>, then restores it.</summary>
    public static string InCulture(string? language, Func<CultureInfo, string> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var culture = CultureOf(language);
        var (previousCulture, previousUiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            return text(culture);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
