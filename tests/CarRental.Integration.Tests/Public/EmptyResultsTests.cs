using System.Net;
using CarRental.Web.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using static CarRental.Integration.Tests.Public.PublicTestData;

namespace CarRental.Integration.Tests.Public;

/// <summary>
/// The empty states of the results page, on the scenario host whose engines answer "nothing available" or "minimum
/// duration not met" for a pick-up at a marker minute.
/// </summary>
[Collection(PublicScenarioCollection.Name)]
public sealed class EmptyResultsTests(PublicScenarioHost scenario)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> GetAsync(string url, string clientIp)
    {
        using var client = scenario.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Add(ClientIpResolver.CloudflareHeader, clientIp);
        var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
    }

    private static string At(int days, int minute) => LocalDate(days)[..^2] + minute.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_stay_shorter_than_the_minimum_duration_states_the_minimum_and_offers_to_extend_it()
    {
        var pickup = At(30, PublicScenarioHost.MinimumDurationMinute);

        var html = await GetAsync($"/fr/reserver?lieu=TUN&depart={pickup}&retour={At(32, 0)}", "198.51.100.30");

        html.ShouldContain("Durée minimale non atteinte");
        html.ShouldContain($"une location dure au moins {PublicScenarioHost.RequiredMinimumDays} jours");
        var extended = SearchExtendedTo(pickup, PublicScenarioHost.RequiredMinimumDays);
        html.ShouldContain($"href=\"/fr/reserver?lieu=TUN&depart={Escape(pickup)}&retour={Escape(extended)}\"");
        html.ShouldContain($"Voir les prix pour {PublicScenarioHost.RequiredMinimumDays} jours");
        OfferCodes(html).ShouldBeEmpty();
    }

    [Fact]
    public async Task Dates_without_any_available_car_say_so_and_offer_whatsapp()
    {
        var html = await GetAsync($"/en/book?lieu=TUN&depart={At(31, PublicScenarioHost.NoAvailabilityMinute)}&retour={At(35, 0)}&cat=ECMR", "198.51.100.31");

        html.ShouldContain("No car available for these dates");
        html.ShouldContain("https://wa.me/21600000000?text=Hello%2C%20I%20would%20like%20to%20book%20a%20car%20%28category%20ECMR%29");
        OfferCodes(html).ShouldBeEmpty();
    }

    [Fact]
    public async Task Filters_that_exclude_every_available_category_offer_to_reset_them()
    {
        var html = await GetAsync($"/fr/reserver?lieu=TUN&depart={At(33, 0)}&retour={At(36, 0)}&places=9&boite=auto", "198.51.100.32");

        html.ShouldContain("Aucune voiture ne correspond à vos filtres");
        html.ShouldContain($"href=\"/fr/reserver?lieu=TUN&depart={Escape(At(33, 0))}&retour={Escape(At(36, 0))}\"");
    }

    private static string SearchExtendedTo(string pickup, int days) =>
        DateTime.ParseExact(pickup, "yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            .AddDays(days)
            .ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static string Escape(string value) => value.Replace(":", "%3A", StringComparison.Ordinal);
}
