using System.Net;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Integration.Tests.Bookings;
using CarRental.Integration.Tests.Public;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.PublicBooking.Visitor;

namespace CarRental.Integration.Tests.PublicBooking;

/// <summary>
/// The booking funnel when online payment is switched off (<c>ClicToPay:Enabled</c> false on the host of the public
/// scenarios, ADR-0049): the payment step says so, sends the request without pre-authorization (no page of the bank)
/// and its confirmation tells that a payment link follows the validation. Everything goes through that host (its
/// services and its pages), whatever database it runs on.
/// </summary>
[Collection(PublicScenarioCollection.Name)]
public sealed class GatewayDisabledFunnelTests(PublicScenarioHost scenario)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_online_payment_the_request_is_sent_without_pre_authorization_and_waits_for_its_link()
    {
        // Steps 1 and 2 through the services (their pages are covered on the shared host; the limits of this host are low).
        var (token, _) = await FunnelWorld.StartDemoAsync(scenario.Factory, BookingWorld.InDays(96), BookingWorld.InDays(99), Ct);

        using var visitor = Create(scenario.Factory);
        var payment = $"/agence-demo-tunis/fr/reserver/{token}/paiement";
        var html = await visitor.PageAsync(payment, Ct);
        var page = WebUtility.HtmlDecode(html);
        page.ShouldContain("data-testid=\"payment-link-mode\"");
        page.ShouldContain("Aucun paiement n'est demandé maintenant : envoyez votre demande.");
        page.ShouldNotContain("pré-autoris");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["terms"] = "1",
            ["cf-turnstile-response"] = "XXXX.DUMMY.TOKEN.XXXX",
            ["__RequestVerificationToken"] = Support.HttpSignIn.AntiforgeryToken(html),
        });
        using var submitted = await visitor.Client.PostAsync(new Uri(payment, UriKind.Relative), form, Ct);
        submitted.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var confirmationPath = Location(submitted);
        confirmationPath.ShouldStartWith("/agence-demo-tunis/fr/reserver/confirmation/LOC-", customMessage: "no page of the bank");

        var confirmation = await visitor.TextAsync(confirmationPath, Ct);
        confirmation.ShouldContain("Demande reçue");
        confirmation.ShouldContain("data-state=\"None\"");
        confirmation.ShouldContain("Aucun paiement n'est demandé maintenant");

        await using var check = FunnelWorld.CreateScope(scenario.Factory, FunnelWorld.Funnel);
        var number = confirmationPath[(confirmationPath.LastIndexOf('/') + 1)..];
        var agency = (await check.ServiceProvider.GetRequiredService<IReferentialQueries>().GetAgenciesAsync(Ct)).Single(a => a.Name == PublicTestData.DemoAgencyName);
        var booking = await check.ServiceProvider.GetRequiredService<IBookingsQueries>().FindByNumberAsync(agency.Id, number, Ct);
        booking.ShouldNotBeNull().Status.ShouldBe(BookingStatusInfo.PendingValidation);
        (await check.ServiceProvider.GetRequiredService<IOnlinePayments>().GetBookingOnlinePaymentsAsync(booking.Id, Ct))
            .ShouldBeEmpty("nothing was asked of the bank");
    }
}
