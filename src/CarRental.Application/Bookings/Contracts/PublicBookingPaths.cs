namespace CarRental.Application.Bookings.Contracts;

/// <summary>
/// Local paths of the public booking pages that the application layer writes into links (e-mails, payment return
/// addresses, notifications of the phase 4); the web host maps the same routes (checked by its tests). French and
/// English slugs, as everywhere on the public site.
/// </summary>
public static class PublicBookingPaths
{
    /// <summary>Query of a "Ma réservation" link opening the documents section directly.</summary>
    public const string DocumentsSection = "pieces";

    /// <summary>Placeholder of the payment id in a payment return path (replaced by the Payments module).</summary>
    public const string PaymentIdPlaceholder = "{paymentId}";

    /// <summary>The access page of "Ma réservation": <c>/fr/ma-reservation</c>, <c>/en/my-booking</c>.</summary>
    public static string MyBooking(string culture) => IsEnglish(culture) ? "/en/my-booking" : $"/{Normalize(culture)}/ma-reservation";

    /// <summary>The booking of the session: <c>/fr/ma-reservation/dossier</c>, <c>/en/my-booking/file</c>.</summary>
    public static string MyBookingFile(string culture) => IsEnglish(culture) ? "/en/my-booking/file" : $"/{Normalize(culture)}/ma-reservation/dossier";

    /// <summary>A link of an e-mail: <c>/fr/ma-reservation/acces/{token}</c> (the documents section when <paramref name="documents"/>).</summary>
    public static string AccessLink(string culture, string token, bool documents = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var path = IsEnglish(culture) ? $"/en/my-booking/access/{token}" : $"/{Normalize(culture)}/ma-reservation/acces/{token}";
        return documents ? $"{path}?section={DocumentsSection}" : path;
    }

    /// <summary>The confirmation of a request: <c>/fr/reserver/confirmation/{numero}</c>, <c>/en/book/confirmation/{number}</c>.</summary>
    public static string Confirmation(string culture, string number)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        return IsEnglish(culture) ? $"/en/book/confirmation/{number}" : $"/{Normalize(culture)}/reserver/confirmation/{number}";
    }

    /// <summary>The page showing the outcome of an online payment (payment links): <c>/fr/paiement/{paymentId}</c>.</summary>
    public static string PaymentResult(string culture) =>
        IsEnglish(culture) ? $"/en/payment/{PaymentIdPlaceholder}" : $"/{Normalize(culture)}/paiement/{PaymentIdPlaceholder}";

    private static bool IsEnglish(string? culture) => string.Equals(culture?.Trim(), "en", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? culture) =>
        culture?.Trim().ToLowerInvariant() is { Length: 2 } code && code.All(char.IsAsciiLetterLower) ? code : "fr";
}
