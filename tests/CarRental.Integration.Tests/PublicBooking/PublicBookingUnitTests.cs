using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Notifications;
using CarRental.Domain.Common;
using CarRental.Web.Components.Emails;
using CarRental.Web.Components.Public;
using CarRental.Web.Components.Public.Booking;
using CarRental.Web.Endpoints.PublicSite;
using CarRental.Web.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CarRental.Integration.Tests.PublicBooking;

/// <summary>
/// The pieces of the public booking pages that need no host: the addresses in both languages, what a visitor may see
/// of an error, dates typed without a date picker, the API key check, the redaction of tokens in the telemetry and the
/// e-mails rendered in the language of the customer.
/// </summary>
public sealed class PublicBookingUnitTests
{
    private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdE";

    [Fact]
    public void The_funnel_and_my_booking_have_an_address_in_each_language()
    {
        BookingRoutes.Step("fr", Token, FunnelStep.Options).ShouldBe($"/fr/reserver/{Token}/options");
        BookingRoutes.Step("en", Token, FunnelStep.Drivers).ShouldBe($"/en/book/{Token}/drivers");
        BookingRoutes.Step("fr", Token, FunnelStep.Payment).ShouldBe($"/fr/reserver/{Token}/paiement");
        BookingRoutes.PaymentRedirect("en", Guid.Empty).ShouldBe("/en/book/redirect/00000000-0000-0000-0000-000000000000");
        BookingRoutes.Confirmation("fr", "LOC-2027-000001").ShouldBe("/fr/reserver/confirmation/LOC-2027-000001");
        BookingRoutes.MyBookingCancellation("en").ShouldBe("/en/my-booking/file/cancellation");
        BookingRoutes.MyBookingDocuments("fr").ShouldBe("/fr/ma-reservation/dossier#pieces");
        PublicBookingPaths.AccessLink("en", Token, documents: true).ShouldBe($"/en/my-booking/access/{Token}?section=pieces");
    }

    [Theory]
    [InlineData("/fr/reserver/" + Token + "/conducteurs", "en", "/en/book/" + Token + "/drivers")]
    [InlineData("/en/book/" + Token + "/payment?erreur=x", "fr", "/fr/reserver/" + Token + "/paiement?erreur=x")]
    [InlineData("/fr/ma-reservation/dossier/annulation", "en", "/en/my-booking/file/cancellation")]
    [InlineData("/en/my-booking/access/" + Token, "fr", "/fr/ma-reservation/acces/" + Token)]
    [InlineData("/fr/reserver/confirmation/LOC-2027-000001", "en", "/en/book/confirmation/LOC-2027-000001")]
    public void The_language_switch_translates_the_steps(string path, string culture, string expected) =>
        PublicRoutes.Localize(path, culture).ShouldBe(expected);

    [Theory]
    [InlineData("booking.funnel.terms_required", true)]
    [InlineData("booking.access.locked", true)]
    [InlineData("customers.document_image.too_large", true)]
    [InlineData("availability.not_available", true)]
    [InlineData("customers.privacy.blacklisted", false)]
    [InlineData("customers.document.number_in_use", false)]
    [InlineData("booking.forbidden", false)]
    [InlineData("<script>", false)]
    [InlineData(null, false)]
    public void A_visitor_sees_the_text_of_known_errors_only(string? code, bool visible) => PublicForms.IsVisible(code).ShouldBe(visible);

    [Fact]
    public void An_unexpected_error_is_shown_as_the_generic_one()
    {
        PublicForms.VisibleCode(Error.Validation("customers.privacy.blacklisted", "x")).ShouldBe(PublicForms.GenericError);
        PublicForms.VisibleCode(Error.Validation("booking.funnel.upload_limit", "x")).ShouldBe("booking.funnel.upload_limit");
    }

    [Theory]
    [InlineData("1984-03-14", "1984-03-14")]
    [InlineData("14/03/1984", "1984-03-14")]
    [InlineData("4/3/1984", "1984-03-04")]
    [InlineData("14.03.1984", "1984-03-14")]
    [InlineData("31/02/1984", "31/02/1984")]
    [InlineData(null, null)]
    public void Dates_typed_without_a_date_picker_are_understood(string? typed, string? expected) =>
        BookingFunnelEndpoints.IsoDate(typed).ShouldBe(expected);

    [Fact]
    public void The_quote_api_accepts_its_configured_keys_only()
    {
        var options = new PublicApiOptions();
        options.QuoteApiKeys.Add("first-key");
        options.QuoteApiKeys.Add("second-key");

        PublicQuoteApi.IsAuthorized(Request("second-key"), options).ShouldBeTrue();
        PublicQuoteApi.IsAuthorized(Request("second-ke"), options).ShouldBeFalse();
        PublicQuoteApi.IsAuthorized(Request(null), options).ShouldBeFalse();
        PublicQuoteApi.IsAuthorized(Request("first-key"), new PublicApiOptions()).ShouldBeFalse("no key configured: every call is refused");

        static HttpRequest Request(string? key)
        {
            var context = new DefaultHttpContext();
            if (key is not null)
            {
                context.Request.Headers[PublicQuoteApi.ApiKeyHeader] = key;
            }

            return context.Request;
        }
    }

    [Theory]
    [InlineData("/fr/reserver/" + Token + "/options", "/fr/reserver/{token}/options")]
    [InlineData("/en/my-booking/access/" + Token, "/en/my-booking/access/{token}")]
    [InlineData("/fr/reserver", null)]
    [InlineData("/fr/reserver/confirmation/LOC-2027-000001", null)]
    [InlineData(null, null)]
    public void Tokens_never_reach_the_telemetry(string? path, string? expected) => SecretPaths.Redact(path).ShouldBe(expected);

    [Fact]
    public async Task The_access_email_is_written_in_the_language_of_the_customer_with_absolute_links()
    {
        var composer = Composer(baseUrl: null);

        var french = await composer.ComposeAccessAsync(
            new AccessEmail("LOC-2027-000123", "fr", $"/fr/ma-reservation/acces/{Token}", "042817", new DateTimeOffset(2027, 3, 1, 9, 30, 0, TimeSpan.Zero), "https://www.agence.tn/"),
            new CustomerRecipient("amel@example.test", "Amel Ben Salah"),
            TestContext.Current.CancellationToken);
        var english = await composer.ComposeAccessAsync(
            new AccessEmail("LOC-2027-000123", "en", $"/en/my-booking/access/{Token}", "042817", new DateTimeOffset(2027, 3, 1, 9, 30, 0, TimeSpan.Zero), "https://www.agence.tn/"),
            new CustomerRecipient("amel@example.test", null),
            TestContext.Current.CancellationToken);

        french.Subject.ShouldBe("Agence Démo — Votre accès à la réservation LOC-2027-000123");
        french.To.ShouldBe("amel@example.test");
        french.ToName.ShouldBe("Amel Ben Salah");
        french.TextBody.ShouldNotBeNull().ShouldContain("Bonjour Amel Ben Salah,");
        french.TextBody.ShouldContain("Votre code : 042817");
        french.TextBody.ShouldContain($"https://www.agence.tn/fr/ma-reservation/acces/{Token}");
        french.TextBody.ShouldContain("lundi 1 mars 2027 à 10:30", customMessage: "Tunis time");
        french.HtmlBody.ShouldStartWith("<!DOCTYPE html>");
        french.HtmlBody.ShouldContain("<html lang=\"fr\">");
        french.HtmlBody.ShouldContain($"href=\"https://www.agence.tn/fr/ma-reservation/acces/{Token}\"");
        english.Subject.ShouldBe("Agence Démo — Your access to booking LOC-2027-000123");
        english.TextBody.ShouldNotBeNull().ShouldContain("Hello,");
        english.TextBody.ShouldContain("Your code: 042817");
        english.HtmlBody.ShouldContain("<html lang=\"en\">");
    }

    [Fact]
    public async Task A_refusal_email_never_tells_the_reason_and_a_cancellation_tells_the_refund()
    {
        var composer = Composer(baseUrl: new Uri("https://www.agence.tn"));
        var recipient = new CustomerRecipient("amel@example.test", "Amel");

        var refused = await composer.ComposeNotificationAsync(Notification(BookingNotificationKind.Refused) with { Message = "Liste noire" }, recipient, TestContext.Current.CancellationToken);
        var cancelled = await composer.ComposeNotificationAsync(
            Notification(BookingNotificationKind.CancellationConfirmed) with { Amount = Money.Tnd(320m), Fee = Money.Tnd(80m), Refund = CancellationRefundState.Requested },
            recipient,
            TestContext.Current.CancellationToken);
        var link = await composer.ComposeNotificationAsync(
            Notification(BookingNotificationKind.PaymentLink) with { ActionUrl = "https://test.clictopay.com/pay/1", Amount = Money.Tnd(400m), ActionExpiresAtUtc = new DateTimeOffset(2027, 3, 3, 9, 0, 0, TimeSpan.Zero) },
            recipient,
            TestContext.Current.CancellationToken);

        refused.Subject.ShouldBe("Agence Démo — Votre demande LOC-2027-000123");
        refused.TextBody.ShouldNotBeNull().ShouldNotContain("Liste noire");
        refused.TextBody.ShouldContain("libéré");
        refused.TextBody.ShouldContain("https://www.agence.tn/fr");
        cancelled.TextBody.ShouldNotBeNull().ShouldContain("Frais d'annulation : 80,000 TND");
        cancelled.TextBody.ShouldContain("Le remboursement de 320,000 TND a été demandé à votre banque");
        link.TextBody.ShouldNotBeNull().ShouldContain("https://test.clictopay.com/pay/1");
        link.TextBody.ShouldContain("Montant à payer : 400,000 TND");
    }

    private static BookingCustomerNotification Notification(BookingNotificationKind kind) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "LOC-2027-000123", kind, "fr", new DateTimeOffset(2027, 3, 1, 9, 0, 0, TimeSpan.Zero), BaseUrl: "https://ignored.example/");

    private static CustomerEmailComposer Composer(Uri? baseUrl)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var site = Options.Create(new PublicSiteOptions { AgencyName = "Agence Démo", BaseUrl = baseUrl });
        return new CustomerEmailComposer(services, NullLoggerFactory.Instance, site);
    }
}
