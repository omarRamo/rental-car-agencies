using System.Net;
using System.Text.RegularExpressions;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Integration.Tests.PublicBooking;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Public.PublicTestData;

namespace CarRental.Integration.Tests.Public;

/// <summary>
/// The site of the second demo agency on the demo data (ADR-0067): it offers its own cars months ahead, as the site of
/// the main demo agency does, and a visitor books one. Its demo vehicles once had documents expiring 60 to 104 days after
/// the seed: past that, the availability engine counted no vehicle and its site found nothing.
/// </summary>
public sealed partial class SecondDemoAgencySiteTests(IntegrationHost host)
{
    /// <summary>Beyond the 104 days the demo fleet of Sousse used to be rentable for; no other test books it at these dates.</summary>
    private const int SearchDay = 150;

    private const int BookingDay = 160;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_second_demo_agencys_site_offers_its_cars_and_a_search_at_SOU_finds_ECMR_and_CDMR()
    {
        var (sousse, categories) = await SecondDemoAgencyAsync();
        var (pickup, dropOff) = (LocalDate(SearchDay), LocalDate(SearchDay + 3));
        var rental = Period.Create(Instant(pickup), Instant(dropOff)).Value;
        using var client = host.CreateClient();

        var home = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri($"/{SecondDemoSlug}/fr", UriKind.Relative), Ct));
        var results = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri($"/{SecondDemoSlug}/fr/reserver?lieu=SOU&depart={pickup}&retour={dropOff}", UriKind.Relative), Ct));

        HomePrice().Count(home).ShouldBe(2, "the home page shows both categories of the agency with a price per day");
        home.ShouldContain("cat=ECMR");
        home.ShouldContain("cat=CDMR");
        OfferCodes(results).ShouldBe(["ECMR", "CDMR"], ignoreOrder: true);
        OfferTotals(results).Count.ShouldBe(2, "every offer is priced by the published plan of the agency");
        results.ShouldContain($"action=\"/{SecondDemoSlug}/fr/reserver/selection\"");

        // The availability engine, read as the system: every vehicle of the agency counts over the whole stay.
        await using var scope = host.CreateScope();
        var fleet = scope.ServiceProvider.GetRequiredService<IFleetQueries>();
        var availability = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();
        (await availability.GetAvailableCategoriesAsync(sousse, rental, SalesChannel.Web, Ct)).Select(a => a.Category.AcrissCode).ShouldBe(["ECMR", "CDMR"], ignoreOrder: true);
        foreach (var category in categories)
        {
            var vehicles = (await fleet.GetVehiclesAsync(category.Id, Ct)).Count;
            var profile = (await availability.GetCapacityProfileAsync(category.Id, rental, SalesChannel.Web, Ct)).ShouldNotBeNull();
            profile.ShouldAllBe(segment => segment.Capacity == vehicles, $"{category.AcrissCode}: {vehicles} vehicles, capacity {string.Join(", ", profile.Select(s => s.Capacity))}");
        }
    }

    [Fact]
    public async Task A_visitor_books_a_car_of_the_second_demo_agency_on_its_site()
    {
        var (sousse, categories) = await SecondDemoAgencyAsync();
        var suffix = Visitor.Unique();
        var email = $"sousse.{suffix.ToLowerInvariant()}@example.test";
        var site = $"/{SecondDemoSlug}/fr/reserver";
        using var visitor = Visitor.Create(host.Factory);

        var token = await visitor.ChooseAsync($"lieu=SOU&depart={LocalDate(BookingDay)}&retour={LocalDate(BookingDay + 4)}", "ECMR", Ct, site: SecondDemoSlug);
        using var toDrivers = await visitor.SubmitAsync($"{site}/{token}/options", [new("action", "continue")], Ct);
        Visitor.Location(toDrivers).ShouldBe($"{site}/{token}/conducteurs");
        using var toPayment = await visitor.SubmitAsync($"{site}/{token}/conducteurs", [.. Visitor.Driver("main", suffix, email), new("consent.documents", "1")], Ct);
        Visitor.Location(toPayment).ShouldBe($"{site}/{token}/paiement", customMessage: Visitor.MainText(await visitor.PageAsync($"{site}/{token}/conducteurs", Ct)));
        using var submitted = await visitor.SubmitAsync($"{site}/{token}/paiement", [new("terms", "1"), new("cf-turnstile-response", "XXXX.DUMMY.TOKEN.XXXX")], Ct);
        var redirect = Visitor.Location(submitted);
        redirect.ShouldStartWith($"{site}/redirection/");
        var bankPage = WebUtility.HtmlDecode(GatewayPage().Match(await visitor.PageAsync(redirect, Ct)).Groups["url"].Value);
        using var paid = await visitor.SubmitAsync(bankPage, [new("action", "pay")], Ct);
        using var back = await visitor.Client.GetAsync(new Uri(Visitor.Location(paid), UriKind.Relative), Ct);
        var confirmation = ConfirmationNumber().Match(Visitor.Location(back));
        confirmation.Success.ShouldBeTrue(Visitor.Location(back));
        (await visitor.TextAsync(Visitor.Location(back), Ct)).ShouldContain("Demande reçue");

        await using var scope = host.CreateScope();
        var booking = (await scope.ServiceProvider.GetRequiredService<IBookingsQueries>().FindByNumberAsync(sousse, confirmation.Groups["number"].Value, Ct)).ShouldNotBeNull();
        booking.AgencyId.ShouldBe(sousse);
        booking.CategoryId.ShouldBe(categories.Single(c => c.AcrissCode == "ECMR").Id);
        booking.Status.ShouldBe(BookingStatusInfo.PendingValidation);
        (await scope.ServiceProvider.GetRequiredService<IOnlinePayments>().GetBookingOnlinePaymentsAsync(booking.Id, Ct))
            .ShouldHaveSingleItem().Status.ShouldBe(PaymentStatusInfo.PreAuthorized);
    }

    private async Task<(Guid Agency, IReadOnlyList<VehicleCategoryInfo> Categories)> SecondDemoAgencyAsync()
    {
        await using var scope = host.CreateScope();
        var agency = (await scope.ServiceProvider.GetRequiredService<IReferentialQueries>().GetAgenciesAsync(Ct)).Single(a => a.Name == "Agence Démo Sousse");
        var categories = await scope.ServiceProvider.GetRequiredService<IFleetQueries>().GetCategoriesAsync(agency.Id, cancellationToken: Ct);
        categories.Select(c => c.AcrissCode).ShouldBe(["ECMR", "CDMR"]);
        return (agency.Id, categories);
    }

    [GeneratedRegex("class=\"category-card__price\">dès <strong>[^<]+ TND</strong>")]
    private static partial Regex HomePrice();

    [GeneratedRegex("<meta http-equiv=\"refresh\" content=\"0;url=(?<url>[^\"]+)\"")]
    private static partial Regex GatewayPage();

    [GeneratedRegex("/agence-demo-sousse/fr/reserver/confirmation/(?<number>LOC-[0-9]{4}-[0-9]{6})$")]
    private static partial Regex ConfirmationNumber();
}
