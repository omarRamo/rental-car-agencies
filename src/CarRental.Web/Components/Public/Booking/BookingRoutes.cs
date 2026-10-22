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
/// and English, on the site of an agency (ADR-0067): <c>/agence-1/fr/reserver/{token}/conducteurs</c> is
/// <c>/agence-1/en/book/{token}/drivers</c>. The pages also answer the slugs of the other language and redirect to the
/// canonical address (the language switcher translates them, <see cref="PublicRoutes.Localize"/>). The paths written by
/// the application layer come from <see cref="PublicBookingPaths"/>, which uses the same slugs.
/// </summary>
public static class BookingRoutes
{
    public const string Options = "options";

    /// <summary>Upload endpoint of the photos (same slug in every language: called by the script or a photo form).</summary>
    public const string Uploads = "pieces";

    /// <summary>The prefixes of the funnel routes: <c>/{agency}/{culture}/reserver</c> and <c>/{agency}/{culture}/book</c>.</summary>
    public static readonly string[] FunnelPrefixes = [$"{PublicRoutes.RoutePrefix}/reserver", $"{PublicRoutes.RoutePrefix}/book"];

    /// <summary>The prefixes of "Ma réservation": <c>/{agency}/{culture}/ma-reservation</c> and <c>/{agency}/{culture}/my-booking</c>.</summary>
    public static readonly string[] MyBookingPrefixes = [$"{PublicRoutes.RoutePrefix}/ma-reservation", $"{PublicRoutes.RoutePrefix}/my-booking"];

    /// <summary>The prefixes of the page of a booking: <c>…/ma-reservation/dossier</c> and <c>…/my-booking/file</c>.</summary>
    public static readonly string[] MyBookingFilePrefixes = [$"{MyBookingPrefixes[0]}/dossier", $"{MyBookingPrefixes[1]}/file"];

    /// <summary>Actions of the page of a booking (POST, same slug in every language).</summary>
    public static class Actions
    {
        public const string Documents = "pieces";
        public const string InformationProvided = "informations";
        public const string PayBalance = "paiement";
        public const string Cancel = "annulation";
        public const string SignOut = "deconnexion";
    }

    public static string Step(string agency, string culture, string token, FunnelStep step) => step switch
    {
        FunnelStep.Options => $"{Prefix(agency, culture)}/{token}/{Options}",
        FunnelStep.Drivers => $"{Prefix(agency, culture)}/{token}/{PublicRoutes.StepSlug("conducteurs", culture)}",
        _ => $"{Prefix(agency, culture)}/{token}/{PublicRoutes.StepSlug("paiement", culture)}",
    };

    public static string UploadsPath(string agency, string culture, string token) => $"{Prefix(agency, culture)}/{token}/{Uploads}";

    /// <summary>The page that sends the browser to the payment page of the bank (GET, never a redirect of a form).</summary>
    public static string PaymentRedirect(string agency, string culture, Guid paymentId) =>
        $"{Prefix(agency, culture)}/{PublicRoutes.StepSlug("redirection", culture)}/{paymentId:D}";

    /// <summary>The confirmation of a request; also the return and failure address of its guarantee.</summary>
    public static string Confirmation(string agency, string culture, string number) => PublicBookingPaths.Confirmation(agency, culture, number);

    /// <summary>A new attempt of the guarantee from the confirmation page (POST).</summary>
    public static string ConfirmationPayment(string agency, string culture, string number) =>
        $"{Confirmation(agency, culture, number)}/{PublicRoutes.StepSlug("paiement", culture)}";

    public static string MyBooking(string agency, string culture) => PublicBookingPaths.MyBooking(agency, culture);

    public static string MyBookingCode(string agency, string culture) => $"{MyBooking(agency, culture)}/code";

    public static string MyBookingCode(string agency, string culture, Guid requestId) => $"{MyBookingCode(agency, culture)}?demande={requestId:D}";

    public static string MyBookingFile(string agency, string culture) => PublicBookingPaths.MyBookingFile(agency, culture);

    /// <summary>An action of the page of the booking (POST): <c>/{agency}/fr/ma-reservation/dossier/{action}</c>.</summary>
    public static string MyBookingAction(string agency, string culture, string action) => $"{MyBookingFile(agency, culture)}/{action}";

    /// <summary>The cancellation page (fee shown first) of the booking: <c>/{agency}/fr/ma-reservation/dossier/annulation</c>.</summary>
    public static string MyBookingCancellation(string agency, string culture) =>
        $"{MyBookingFile(agency, culture)}/{PublicRoutes.StepSlug("annulation", culture)}";

    /// <summary>The documents section of the page of the booking.</summary>
    public static string MyBookingDocuments(string agency, string culture) => $"{MyBookingFile(agency, culture)}#{PublicBookingPaths.DocumentsSection}";

    public static string MyBookingDocument(string agency, string culture, string documentId) =>
        $"{MyBookingFile(agency, culture)}/documents/{Uri.EscapeDataString(documentId)}";

    /// <summary><c>/{agency}/fr/reserver</c> or <c>/{agency}/en/book</c>.</summary>
    public static string Prefix(string agency, string culture) => PublicRoutes.PathFor(agency, PublicPage.Results, culture);

    /// <summary>Whether <paramref name="culture"/> is English (the slugs).</summary>
    public static bool IsEnglish(string culture) => string.Equals(culture, "en", StringComparison.OrdinalIgnoreCase);
}
