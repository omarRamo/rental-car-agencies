using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Web.Components.Public.Site;

/// <summary>
/// Display rules of the public site: amounts in TND with their millimes in the current culture (<c>681,400 TND</c> in
/// French, ADR-0015), an indicative euro equivalent rounded to the euro, dates and times in Tunis time.
/// </summary>
public static class PublicFormat
{
    /// <summary>An amount with its millimes, e.g. <c>1 234,500 TND</c> in French, <c>1,234.500 TND</c> in English.</summary>
    public static string Price(Money amount, CultureInfo? culture = null) => amount.ToString("N", culture ?? CultureInfo.CurrentCulture);

    /// <summary>
    /// Euros for <paramref name="amount"/> at <paramref name="tndPerEur"/> dinars for one euro, rounded to the euro (away
    /// from zero: an equivalent is indicative, millimes would suggest a precision it does not have); <see langword="null"/>
    /// without a positive rate or a TND amount.
    /// </summary>
    public static decimal? EuroEquivalent(Money amount, decimal tndPerEur) =>
        tndPerEur > 0m && amount.Currency == Currency.Tnd
            ? Math.Round(amount.Amount / tndPerEur, 0, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>Whole euros with the symbol placed by the culture: <c>200 €</c> in French, <c>€200</c> in English.</summary>
    public static string Euros(decimal euros, CultureInfo? culture = null)
    {
        var format = (NumberFormatInfo)(culture ?? CultureInfo.CurrentCulture).NumberFormat.Clone();
        format.CurrencySymbol = "€";
        format.CurrencyDecimalDigits = 0;
        return euros.ToString("C0", format);
    }

    /// <summary>The Tunis wall-clock time of an instant.</summary>
    public static DateTime TunisLocal(DateTimeOffset instant) => TunisTime.ToTunis(instant).DateTime;

    /// <summary>A short date in Tunis, e.g. <c>24 octobre 2026</c> / <c>24 October 2026</c>.</summary>
    public static string Date(DateTimeOffset instant, CultureInfo? culture = null) =>
        TunisLocal(instant).ToString("d MMMM yyyy", culture ?? CultureInfo.CurrentCulture);

    /// <summary>Opening hours as <c>08:00</c> (24-hour clock in every culture, as in Tunisia).</summary>
    public static string Time(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>tel:</c> link of a phone number written for humans (<c>+216 70 000 000</c> → <c>tel:+21670000000</c>);
    /// <see langword="null"/> when there is no digit.
    /// </summary>
    public static string? TelephoneLink(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = new string([.. phone.Where(char.IsAsciiDigit)]);
        if (digits.Length == 0)
        {
            return null;
        }

        var international = phone.TakeWhile(c => !char.IsAsciiDigit(c)).Contains('+');
        return international ? $"tel:+{digits}" : $"tel:{digits}";
    }
}
