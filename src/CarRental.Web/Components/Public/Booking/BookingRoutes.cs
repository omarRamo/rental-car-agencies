using CarRental.Application.Bookings.Contracts;

namespace CarRental.Web.Components.Public.Booking;

/// <summary>A step of the booking funnel.</summary>
public enum FunnelStep
{
    Options = 1,
    Drivers = 2,
    Payment = 3,
}

/// <summary>
/// Addresses of the booking funnel (docs/conception/phase-2-vitrine-paiement.md §1) and of "Ma réservation", in French
/// and English: <c>/fr/reserver/{token}/conducteurs</c> is <c>/en/book/{token}/drivers</c>. The pages also answer the
/// slugs of the other language and redirect to the canonical address (the language switcher translates them,
/// <see cref="PublicRoutes.Localize"/>). The paths written by the application layer come from
/// <see cref="PublicBookingPaths"/>, which uses the same slugs.
/// </summary>
public static class BookingRoutes
{
    public const string Options = "options";

    /// <summary>Upload endpoint of the photos (same slug in every language: called by the script or a photo form).</summary>
    public const string Uploads = "pieces";

    /// <summary>The prefixes of the funnel routes: <c>/{culture}/reserver</c> and <c>/{culture}/book</c>.</summary>
    public static readonly string[] FunnelPrefixes = ["/{culture:length(2)}/reserver", "/{culture:length(2)}/book"];

    /// <summary>The prefixes of "Ma réservation": <c>/{culture}/ma-reservation</c> and <c>/{culture}/my-booking</c>.</summary>
    public static readonly string[] MyBookingPrefixes = ["/{culture:length(2)}/ma-reservation", "/{culture:length(2)}/my-booking"];

    /// <summary>The prefixes of the page of a booking: <c>/{culture}/ma-reservation/dossier</c> and <c>/{culture}/my-booking/file</c>.</summary>
    public static readonly string[] MyBookingFilePrefixes = ["/{culture:length(2)}/ma-reservation/dossier", "/{culture:length(2)}/my-booking/file"];

    /// <summary>Actions of the page of a booking (POST, same slug in every language).</summary>
    public static class Actions
    {
        public const string Documents = "pieces";
        public const string InformationProvided = "informations";
        public const string PayBalance = "paiement";
        public const string Cancel = "annulation";
        public const string SignOut = "deconnexion";
    }

    public static string Step(string culture, string token, FunnelStep step) => step switch
    {
        FunnelStep.Options => $"{Prefix(culture)}/{token}/{Options}",
        FunnelStep.Drivers => $"{Prefix(culture)}/{token}/{PublicRoutes.StepSlug("conducteurs", culture)}",
        _ => $"{Prefix(culture)}/{token}/{PublicRoutes.StepSlug("paiement", culture)}",
    };

    public static string UploadsPath(string culture, string token) => $"{Prefix(culture)}/{token}/{Uploads}";

    /// <summary>The page that sends the browser to the payment page of the bank (GET, never a redirect of a form).</summary>
    public static string PaymentRedirect(string culture, Guid paymentId) => $"{Prefix(culture)}/{PublicRoutes.StepSlug("redirection", culture)}/{paymentId:D}";

    /// <summary>The confirmation of a request; also the return and failure address of its guarantee.</summary>
    public static string Confirmation(string culture, string number) => PublicBookingPaths.Confirmation(culture, number);

    /// <summary>A new attempt of the guarantee from the confirmation page (POST).</summary>
    public static string ConfirmationPayment(string culture, string number) => $"{Confirmation(culture, number)}/{PublicRoutes.StepSlug("paiement", culture)}";

    public static string MyBooking(string culture) => PublicBookingPaths.MyBooking(culture);

    public static string MyBookingCode(string culture) => $"{MyBooking(culture)}/code";

    public static string MyBookingCode(string culture, Guid requestId) => $"{MyBookingCode(culture)}?demande={requestId:D}";

    public static string MyBookingFile(string culture) => PublicBookingPaths.MyBookingFile(culture);

    /// <summary>An action of the page of the booking (POST): <c>/fr/ma-reservation/dossier/{action}</c>.</summary>
    public static string MyBookingAction(string culture, string action) => $"{MyBookingFile(culture)}/{action}";

    /// <summary>The cancellation page (fee shown first) of the booking: <c>/fr/ma-reservation/dossier/annulation</c>.</summary>
    public static string MyBookingCancellation(string culture) => $"{MyBookingFile(culture)}/{PublicRoutes.StepSlug("annulation", culture)}";

    /// <summary>The documents section of the page of the booking.</summary>
    public static string MyBookingDocuments(string culture) => $"{MyBookingFile(culture)}#{PublicBookingPaths.DocumentsSection}";

    public static string MyBookingDocument(string culture, string documentId) => $"{MyBookingFile(culture)}/documents/{Uri.EscapeDataString(documentId)}";

    /// <summary><c>/fr/reserver</c> or <c>/en/book</c>.</summary>
    public static string Prefix(string culture) => PublicRoutes.PathFor(PublicPage.Results, culture);

    /// <summary>Whether <paramref name="culture"/> is English (the slugs).</summary>
    public static bool IsEnglish(string culture) => string.Equals(culture, "en", StringComparison.OrdinalIgnoreCase);
}
