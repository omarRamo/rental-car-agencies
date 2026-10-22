using System.Net;
using System.Text.RegularExpressions;
using CarRental.Integration.Tests.PublicBooking;
using CarRental.Testing;
using CarRental.Web.Endpoints.PublicSite.Turnstile;
using static CarRental.Integration.Tests.Public.PublicTestData;
using static CarRental.Integration.Tests.PublicBooking.Visitor;

namespace CarRental.Integration.Tests.Security;

/// <summary>
/// The pages of the booking funnel and of "Ma réservation" are rendered for one visitor (a token in their address, or a
/// session): every one of them is answered <c>no-store</c> and marked <c>noindex, nofollow</c> (R-05 of the security
/// review) — the steps, the page leading to the bank, the confirmation (its own and the neutral one), the access
/// request, the code, the link and the pages of the booking. Their rate limits are checked by the endpoint inventory.
/// </summary>
public sealed partial class PrivatePagesTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_page_of_the_funnel_and_of_my_booking_is_never_stored_nor_indexed()
    {
        var suffix = Unique();
        var email = $"private.{suffix.ToLowerInvariant()}@example.test";
        using var visitor = Create(host.Factory);

        var token = await visitor.ChooseAsync($"lieu=TUN&depart={LocalDate(161)}&retour={LocalDate(164)}", "ECMR", Ct);
        var options = $"/fr/reserver/{token}/options";
        await AssertPrivateAsync(visitor, options);
        using var toDrivers = await visitor.SubmitAsync(options, [new("action", "continue")], Ct);
        var drivers = Location(toDrivers);
        await AssertPrivateAsync(visitor, drivers);
        using var toPayment = await visitor.SubmitAsync(drivers, [.. Driver("main", suffix, email), new("consent.documents", "1")], Ct);
        var payment = Location(toPayment);
        payment.ShouldBe($"/fr/reserver/{token}/paiement", customMessage: MainText(await visitor.PageAsync(drivers, Ct)));
        await AssertPrivateAsync(visitor, payment);

        using var submitted = await visitor.SubmitAsync(payment, [new("terms", "1"), new("cf-turnstile-response", TurnstileTestKeys.DummyToken)], Ct);
        var redirect = Location(submitted);
        redirect.ShouldStartWith("/fr/reserver/redirection/");
        var interstitial = await AssertPrivateAsync(visitor, redirect);
        var bank = WebUtility.HtmlDecode(BankPage().Match(interstitial).Groups["url"].Value);
        using var paid = await visitor.SubmitAsync(bank, [new("action", "pay")], Ct);
        using var back = await visitor.Client.GetAsync(new Uri(Location(paid), UriKind.Relative), Ct);
        var confirmation = Location(back);
        var number = ConfirmationNumber().Match(confirmation).Groups["number"].Value;
        number.ShouldStartWith("LOC-");
        (await AssertPrivateAsync(visitor, confirmation)).ShouldContain("Demande reçue");
        using (var stranger = Create(host.Factory))
        {
            (await AssertPrivateAsync(stranger, confirmation)).ShouldContain("Suivre une demande", customMessage: "another browser gets the neutral page");
        }

        using var customer = Create(host.Factory);
        await AssertPrivateAsync(customer, "/fr/ma-reservation");
        using var asked = await customer.SubmitAsync(
            "/fr/ma-reservation",
            [new("email", email), new("numero", number), new("cf-turnstile-response", TurnstileTestKeys.DummyToken)],
            Ct);
        var codePage = Location(asked);
        await AssertPrivateAsync(customer, codePage);
        var accessEmail = await TestEmails.WaitForAsync(
            host.Factory.EmailDirectory,
            e => e.To.Contains(email, StringComparison.Ordinal) && e.Subject == $"CarRental — Votre accès à la réservation {number}",
            Ct);
        var link = new Uri(TestEmails.Links(accessEmail).Single(l => l.Contains("/ma-reservation/acces/", StringComparison.Ordinal))).PathAndQuery;
        await AssertPrivateAsync(customer, link);
        using var opened = await customer.SubmitAsync(
            codePage,
            "/fr/ma-reservation/code",
            [new("demande", RequestId().Match(codePage).Value), new("code", TestEmails.Code(accessEmail)!)],
            Ct);
        Location(opened).ShouldBe("/fr/ma-reservation/dossier");
        (await AssertPrivateAsync(customer, "/fr/ma-reservation/dossier")).ShouldContain(number);
        await AssertPrivateAsync(customer, "/fr/ma-reservation/dossier/annulation");
    }

    /// <summary>The page at <paramref name="path"/>: 200, never stored by a cache, never indexed nor followed.</summary>
    private static async Task<string> AssertPrivateAsync(Visitor visitor, string path)
    {
        using var response = await visitor.Client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"GET {path}");
        response.Headers.CacheControl.ShouldNotBeNull($"GET {path}").NoStore.ShouldBeTrue($"GET {path}");
        html.ShouldContain("<meta name=\"robots\" content=\"noindex, nofollow\"", customMessage: $"GET {path}");
        return WebUtility.HtmlDecode(html);
    }

    [GeneratedRegex("<meta http-equiv=\"refresh\" content=\"0;url=(?<url>[^\"]+)\"")]
    private static partial Regex BankPage();

    [GeneratedRegex("/fr/reserver/confirmation/(?<number>LOC-[0-9]{4}-[0-9]{6})$")]
    private static partial Regex ConfirmationNumber();

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex RequestId();
}
