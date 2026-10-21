using System.Globalization;
using System.Text;
using CarRental.Domain.Common;

namespace CarRental.Domain.Messaging.Agent;

/// <summary>
/// The deep link of the booking funnel sent by the WhatsApp agent (SPEC §5, docs/conception/phase-2-vitrine-paiement.md
/// §1): <c>{base}/fr/reserver?lieu=TUN&amp;depart=2027-07-01T10:00&amp;retour=2027-07-05T10:00&amp;cat=ECMR&amp;devis=Q7F3K2&amp;src=whatsapp</c>.
/// Parameter names are the same in every language; the page slug is the canonical one of the language (<c>/en/book</c>;
/// Arabic and darija use the French site until Arabic is enabled). Dates are Tunis wall-clock times; the return place
/// (<c>retour_lieu</c>) is given only when it differs. The server prices and checks everything again: the link only
/// carries the search and the code of the locked quote.
/// </summary>
public static class BookingDeepLink
{
    public const string Source = "whatsapp";

    /// <summary>Format of the dates of a deep link (Tunis wall-clock time, no offset).</summary>
    public const string DateTimeFormat = "yyyy-MM-dd'T'HH:mm";

    /// <summary>The culture segment and slug of the results page of the funnel for <paramref name="language"/>.</summary>
    public static string PathFor(AgentLanguage language) => language == AgentLanguage.English ? "en/book" : "fr/reserver";

    /// <summary>The funnel page itself (no search): the secure place where documents are uploaded.</summary>
    public static Uri FunnelPage(Uri baseUrl, AgentLanguage language)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return new Uri(Root(baseUrl) + PathFor(language));
    }

    public static Uri Build(
        Uri baseUrl,
        AgentLanguage language,
        string pickupLocationCode,
        string? returnLocationCode,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        string categoryCode,
        string quoteCode)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(pickupLocationCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(quoteCode);
        if (!baseUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The base address of the public site is absolute.", nameof(baseUrl));
        }

        if (returnAt <= pickupAt)
        {
            throw new ArgumentException("The return is after the pick-up.", nameof(returnAt));
        }

        var query = new StringBuilder();
        void Add(string name, string value)
        {
            query.Append(query.Length == 0 ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }

        var pickup = pickupLocationCode.Trim().ToUpperInvariant();
        Add("lieu", pickup);
        if (returnLocationCode?.Trim().ToUpperInvariant() is { Length: > 0 } dropOff && dropOff != pickup)
        {
            Add("retour_lieu", dropOff);
        }

        Add("depart", Local(pickupAt));
        Add("retour", Local(returnAt));
        Add("cat", categoryCode.Trim().ToUpperInvariant());
        Add("devis", quoteCode.Trim().ToUpperInvariant());
        Add("src", Source);
        return new Uri(Root(baseUrl) + PathFor(language) + query);
    }

    private static string Local(DateTimeOffset instant) =>
        TunisTime.ToTunis(instant).ToString(DateTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>Scheme, host and path of the base address, ending with '/', without query nor fragment.</summary>
    private static string Root(Uri baseUrl)
    {
        var root = baseUrl.GetLeftPart(UriPartial.Path);
        return root.EndsWith('/') ? root : root + "/";
    }
}
