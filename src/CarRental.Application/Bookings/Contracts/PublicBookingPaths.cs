namespace CarRental.Application.Bookings.Contracts;

/// <summary>
/// Local paths of the public booking pages that the application layer writes into links (e-mails, payment return
/// addresses, notifications of the phase 4); the web host maps the same routes (checked by its tests). Every public
/// address starts with the address of the agency's site (<c>Agency.Slug</c>, ADR-0067), then the culture: a link always
/// carries the agency of its booking (<c>/agence-1/fr/ma-reservation</c>). On the agency's own host (ADR-0069) the
/// address segment is left out: an empty <c>agency</c> gives <c>/fr/ma-reservation</c>. French and English slugs, as
/// everywhere on the public site.
/// </summary>
public static class PublicBookingPaths
{
    /// <summary>Query of a "Ma réservation" link opening the documents section directly.</summary>
    public const string DocumentsSection = "pieces";

    /// <summary>Placeholder of the payment id in a payment return path (replaced by the Payments module).</summary>
    public const string PaymentIdPlaceholder = "{paymentId}";

    /// <summary>
    /// The start of every address of the site of <paramref name="agency"/>: <c>/agence-1</c> on the platform host, nothing
    /// (<see cref="string.Empty"/>) for a site served on its own host (empty <paramref name="agency"/>, ADR-0069).
    /// </summary>
    public static string Root(string agency)
    {
        ArgumentNullException.ThrowIfNull(agency);
        return agency.Length == 0 ? string.Empty : "/" + Slug(agency);
    }

    /// <summary>The home page of the site of <paramref name="agency"/> in <paramref name="culture"/>: <c>/agence-1/fr</c> (<c>/fr</c> on its own host).</summary>
    public static string Site(string agency, string culture) => $"{Root(agency)}/{Normalize(culture)}";

    /// <summary>The access page of "Ma réservation": <c>/{agency}/fr/ma-reservation</c>, <c>/{agency}/en/my-booking</c>.</summary>
    public static string MyBooking(string agency, string culture) =>
        $"{Site(agency, culture)}/{(IsEnglish(culture) ? "my-booking" : "ma-reservation")}";

    /// <summary>The booking of the session: <c>/{agency}/fr/ma-reservation/dossier</c>, <c>/{agency}/en/my-booking/file</c>.</summary>
    public static string MyBookingFile(string agency, string culture) =>
        $"{MyBooking(agency, culture)}/{(IsEnglish(culture) ? "file" : "dossier")}";

    /// <summary>
    /// A link of an e-mail: <c>/{agency}/fr/ma-reservation/acces/{token}</c> (the documents section when
    /// <paramref name="documents"/>).
    /// </summary>
    public static string AccessLink(string agency, string culture, string token, bool documents = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var path = $"{MyBooking(agency, culture)}/{(IsEnglish(culture) ? "access" : "acces")}/{token}";
        return documents ? $"{path}?section={DocumentsSection}" : path;
    }

    /// <summary>
    /// The confirmation of a request: <c>/{agency}/fr/reserver/confirmation/{numero}</c>,
    /// <c>/{agency}/en/book/confirmation/{number}</c>.
    /// </summary>
    public static string Confirmation(string agency, string culture, string number)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        return $"{Site(agency, culture)}/{(IsEnglish(culture) ? "book" : "reserver")}/confirmation/{number}";
    }

    /// <summary>The page showing the outcome of an online payment (payment links): <c>/{agency}/fr/paiement/{paymentId}</c>.</summary>
    public static string PaymentResult(string agency, string culture) =>
        $"{Site(agency, culture)}/{(IsEnglish(culture) ? "payment" : "paiement")}/{PaymentIdPlaceholder}";

    private static bool IsEnglish(string? culture) => string.Equals(culture?.Trim(), "en", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? culture) =>
        culture?.Trim().ToLowerInvariant() is { Length: 2 } code && code.All(char.IsAsciiLetterLower) ? code : "fr";

    /// <summary>The address of the agency's site as stored (lower case); never empty, never more than one segment.</summary>
    private static string Slug(string agency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agency);
        var slug = agency.Trim().ToLowerInvariant();
        if (!slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            throw new ArgumentException("The address of an agency's site is lower-case letters, digits and hyphens.", nameof(agency));
        }

        return slug;
    }
}
