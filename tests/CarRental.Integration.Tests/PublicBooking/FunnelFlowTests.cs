using System.Net;
using System.Text.RegularExpressions;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Testing;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Public.PublicTestData;
using static CarRental.Integration.Tests.PublicBooking.Visitor;

namespace CarRental.Integration.Tests.PublicBooking;

/// <summary>
/// The booking funnel over HTTP on the demo agency (ADR-0049, ADR-0050): choice, options, drivers and photos (stored in
/// Azurite), submission with Turnstile, pre-authorization on the payment page of the simulated bank, confirmation and
/// the "request received" e-mail. Each test books its own dates, far enough to never meet another test's.
/// </summary>
public sealed partial class FunnelFlowTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_visitor_books_a_car_and_the_bank_holds_the_guarantee()
    {
        var suffix = Unique();
        var email = $"funnel.{suffix.ToLowerInvariant()}@example.test";
        var (pickup, dropOff) = (LocalDate(76), LocalDate(80));
        using var visitor = Create(host.Factory);

        var token = await visitor.ChooseAsync($"lieu=TUN&depart={pickup}&retour={dropOff}&utm_source=newsletter&utm_campaign=hiver", "ECMR", Ct);
        var options = $"/fr/reserver/{token}/options";
        var optionsPage = await visitor.TextAsync(options, Ct);
        optionsPage.ShouldContain("Étape 1 sur 3");
        optionsPage.ShouldContain("Siège enfant");
        optionsPage.ShouldContain("Prix garanti jusqu'au");
        var initialTotal = Total(optionsPage);

        using var toDrivers = await visitor.SubmitAsync(options, [new("extra.CHILD_SEAT", "1"), new("action", "continue")], Ct);
        toDrivers.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var drivers = $"/fr/reserver/{token}/conducteurs";
        Location(toDrivers).ShouldBe(drivers);

        // A form with missing fields comes back with every error listed and linked to its field.
        using var incomplete = await visitor.SubmitAsync(drivers, [new("main.first_name", "Amel")], Ct);
        Location(incomplete).ShouldBe(drivers);
        var withErrors = await visitor.TextAsync(drivers, Ct);
        withErrors.ShouldContain("Corrigez les points suivants");
        withErrors.ShouldContain("href=\"/fr/reserver/" + token + "/conducteurs#main-last-name\"");
        withErrors.ShouldContain("Ce champ est obligatoire.");
        withErrors.ShouldContain("value=\"Amel\"");
        Total(withErrors).ShouldBeGreaterThan(initialTotal, "the child seat was added to the locked quote");

        // Photos go through the upload endpoint of the session: the answer is a text, never an image address.
        var (status, answer) = await visitor.UploadAsync(drivers, $"/fr/reserver/{token}/pieces", "main.license.front", Jpeg, "image/jpeg", Ct);
        status.ShouldBe(HttpStatusCode.OK);
        answer.GetProperty("received").GetBoolean().ShouldBeTrue();
        answer.GetProperty("message").GetString().ShouldBe("Photo reçue.");
        answer.GetRawText().ShouldNotContain("http");
        answer.GetRawText().ShouldNotContain("persons/");
        var (refusedStatus, refused) = await visitor.UploadAsync(drivers, $"/fr/reserver/{token}/pieces", "main.identity.front", "not an image"u8.ToArray(), "image/jpeg", Ct);
        refusedStatus.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.GetProperty("error").GetString().ShouldBe("customers.document_image.unsupported_type");
        (await visitor.UploadAsync(drivers, $"/fr/reserver/{token}/pieces", "main.identity.front", Png, "image/png", Ct)).Status.ShouldBe(HttpStatusCode.OK);

        using var toPayment = await visitor.SubmitAsync(drivers, [.. Driver("main", suffix, email), new("consent.documents", "1"), new("consent.whatsapp", "1")], Ct);
        var payment = $"/fr/reserver/{token}/paiement";
        Location(toPayment).ShouldBe(payment);
        var paymentPage = await visitor.TextAsync(payment, Ct);
        paymentPage.ShouldContain("pré-autorisation");
        paymentPage.ShouldContain($"Conducteur principal : Amel Funnel{suffix}");
        paymentPage.ShouldContain("href=\"/fr/conditions\"");

        using var withoutTerms = await visitor.SubmitAsync(payment, [new("cf-turnstile-response", "XXXX.DUMMY.TOKEN.XXXX")], Ct);
        Location(withoutTerms).ShouldBe($"{payment}?erreur=booking.funnel.terms_required");

        using var submitted = await visitor.SubmitAsync(payment, [new("terms", "1"), new("cf-turnstile-response", "XXXX.DUMMY.TOKEN.XXXX")], Ct);
        submitted.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var redirect = Location(submitted);
        redirect.ShouldStartWith("/fr/reserver/redirection/");
        submitted.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith("carrental.request=", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));

        // The page of the site leads to the payment page of the bank (never a redirect of a form: CSP form-action 'self').
        var interstitial = await visitor.PageAsync(redirect, Ct);
        var gatewayPage = GatewayPage().Match(interstitial);
        gatewayPage.Success.ShouldBeTrue(interstitial);
        var bankPage = WebUtility.HtmlDecode(gatewayPage.Groups["url"].Value);
        bankPage.ShouldStartWith("/dev/clictopay/pay/");

        using var paid = await visitor.SubmitAsync(bankPage, [new("action", "pay")], Ct);
        using var back = await visitor.Client.GetAsync(new Uri(Location(paid), UriKind.Relative), Ct);
        var confirmationPath = Location(back);
        var number = ConfirmationNumber().Match(confirmationPath).Groups["number"].Value;
        number.ShouldStartWith("LOC-");
        var confirmation = await visitor.TextAsync(confirmationPath, Ct);
        confirmation.ShouldContain("Demande reçue");
        confirmation.ShouldContain("L'agence vous répond sous 2 heures ouvrées.");
        confirmation.ShouldContain("data-state=\"Held\"");

        await using var scope = host.CreateScope();
        var booking = await scope.ServiceProvider.GetRequiredService<IBookingsQueries>().FindByNumberAsync((await DemoAgencyAsync(host, Ct)).Agency.Id, number, Ct);
        booking.ShouldNotBeNull();
        booking.Status.ShouldBe(BookingStatusInfo.PendingValidation);
        booking.Channel.ShouldBe(Domain.Common.SalesChannel.Web);
        booking.HoldExpiresAt.ShouldNotBeNull();
        booking.TotalInclVat.Amount.ShouldBe(Total(withErrors));
        var online = await scope.ServiceProvider.GetRequiredService<IOnlinePayments>().GetBookingOnlinePaymentsAsync(booking.Id, Ct);
        online.ShouldHaveSingleItem().Status.ShouldBe(PaymentStatusInfo.PreAuthorized);
        online[0].Amount.ShouldBe(booking.TotalInclVat);

        // The new driver was created from what was typed, with the photos and the consent.
        var person = await scope.ServiceProvider.GetRequiredService<ICustomersQueries>().GetPersonAsync(booking.MainDriverId!.Value, Ct);
        person.ShouldNotBeNull();
        person.LastName.ShouldBe($"Funnel{suffix}");
        person.Email.ShouldBe(email);

        var received = await TestEmails.WaitForAsync(host.Factory.EmailDirectory, e => e.To.Contains(email, StringComparison.Ordinal) && e.Subject.Contains(number, StringComparison.Ordinal), Ct);
        received.Subject.ShouldBe($"CarRental — Demande {number} reçue");
        received.Text.ShouldContain("Bonjour Amel Funnel" + suffix);
        received.Text.ShouldContain("sous 2 heures ouvrées");
        received.Html.ShouldContain("<html lang=\"fr\">");
        TestEmails.Links(received).ShouldContain("http://localhost/fr/ma-reservation");
    }

    [Fact]
    public async Task The_english_funnel_has_its_own_addresses_and_the_language_switch_keeps_the_step()
    {
        var (pickup, dropOff) = (LocalDate(81), LocalDate(84));
        using var visitor = Create(host.Factory);

        var token = await visitor.ChooseAsync($"lieu=TUN&depart={pickup}&retour={dropOff}", "ECMR", Ct, culture: "en");
        var options = await visitor.TextAsync($"/en/book/{token}/options", Ct);
        options.ShouldContain("Step 1 of 3");
        options.ShouldContain($"href=\"/fr/reserver/{token}/options\"");

        using var toDrivers = await visitor.SubmitAsync($"/en/book/{token}/options", [new("action", "continue")], Ct);
        Location(toDrivers).ShouldBe($"/en/book/{token}/drivers");
        var drivers = await visitor.TextAsync($"/en/book/{token}/drivers", Ct);
        drivers.ShouldContain("Who will drive?");
        drivers.ShouldContain($"href=\"/fr/reserver/{token}/conducteurs\"");

        // A slug of the other language leads to the canonical address of the page.
        using var other = await visitor.Client.GetAsync(new Uri($"/en/reserver/{token}/conducteurs", UriKind.Relative), Ct);
        other.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Location(other).ShouldEndWith($"/en/book/{token}/drivers");
    }

    [Fact]
    public async Task An_unknown_or_expired_funnel_address_leads_back_to_the_search_with_a_message()
    {
        using var visitor = Create(host.Factory);

        using var unknown = await visitor.Client.GetAsync(new Uri($"/fr/reserver/{new string('A', 43)}/options", UriKind.Relative), Ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Location(unknown).ShouldEndWith("/fr/reserver?info=lien");
        var results = await visitor.TextAsync("/fr/reserver?info=lien", Ct);
        results.ShouldContain("Ce lien de réservation n'est plus valable");
    }

    [Fact]
    public async Task A_refused_anti_robot_check_keeps_the_request_unsent()
    {
        var suffix = Unique();
        using var visitor = Create(host.Factory);
        var token = await visitor.ChooseAsync($"lieu=TUN&depart={LocalDate(86)}&retour={LocalDate(89)}", "ECMR", Ct);
        using var saved = await visitor.SubmitAsync($"/fr/reserver/{token}/conducteurs", [.. Driver("main", suffix), new("consent.documents", "1")], Ct);
        Location(saved).ShouldBe($"/fr/reserver/{token}/paiement", customMessage: MainText(await visitor.PageAsync($"/fr/reserver/{token}/conducteurs", Ct)));

        using var robot = await visitor.SubmitAsync(
            $"/fr/reserver/{token}/paiement",
            [new("terms", "1"), new("cf-turnstile-response", FakeTurnstileVerifier.RejectedToken)],
            Ct);

        Location(robot).ShouldBe($"/fr/reserver/{token}/paiement?erreur=booking.funnel.verification_failed");
        (await visitor.TextAsync(Location(robot), Ct)).ShouldContain("La vérification anti-robot a échoué");
        host.Factory.TurnstileVerifier.Calls.ShouldContain(
            c => c.Token == FakeTurnstileVerifier.RejectedToken && c.Action == "booking_submit" && c.Hostname == "localhost",
            "the challenge must have been solved on the host that receives the form");
        (await visitor.PageAsync($"/fr/reserver/{token}/paiement", Ct)).ShouldContain("data-testid=\"funnel-submit\"");
    }

    private static decimal Total(string text)
    {
        var match = TotalCell().Match(text);
        match.Success.ShouldBeTrue("No total on the page.");
        return decimal.Parse(match.Groups["amount"].Value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("data-testid=\"funnel-total\">(?<amount>[0-9 ,.]+) TND<")]
    private static partial Regex TotalCell();

    [GeneratedRegex("<meta http-equiv=\"refresh\" content=\"0;url=(?<url>[^\"]+)\"")]
    private static partial Regex GatewayPage();

    [GeneratedRegex("/fr/reserver/confirmation/(?<number>LOC-[0-9]{4}-[0-9]{6})$")]
    private static partial Regex ConfirmationNumber();
}
