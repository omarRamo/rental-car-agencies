using System.Globalization;
using System.Net;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Components.Public.Site;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Public.PublicTestData;

namespace CarRental.Integration.Tests.Public;

/// <summary>The results page on the demo data: availability and prices recomputed for every search.</summary>
public sealed class ResultsPageTests(IntegrationHost host)
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpStatusCode Status, string Html)> GetAsync(string url)
    {
        using var client = host.CreateClient();
        var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
        return (response.StatusCode, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct)));
    }

    [Fact]
    public async Task Available_categories_are_shown_with_the_total_of_the_pricing_engine()
    {
        var (agency, locations, categories) = await DemoAgencyAsync(host, Ct);
        var airport = locations.Single(l => l.Code == "TUN");
        var (pickup, dropOff) = (LocalDate(20), LocalDate(24));

        var (status, html) = await GetAsync($"/fr/reserver?lieu=TUN&depart={pickup}&retour={dropOff}");

        status.ShouldBe(HttpStatusCode.OK);
        var codes = OfferCodes(html);
        var totals = OfferTotals(html);
        codes.ShouldNotBeEmpty();
        codes.Count.ShouldBe(totals.Count);
        await using var scope = host.CreateScope();
        var pricing = scope.ServiceProvider.GetRequiredService<IPricingService>();
        var expected = new List<Money>();
        foreach (var code in codes)
        {
            var category = categories.Single(c => c.AcrissCode == code);
            var quote = await pricing.QuoteAsync(
                new PriceRequest(agency.Id, category.Id, airport.Id, airport.Id, Instant(pickup), Instant(dropOff), SalesChannel.Web),
                Ct);
            expected.Add(quote.Value.TotalInclVat);
        }

        totals.ShouldBe([.. expected.Select(total => Plain(PublicFormat.Price(total, French)))]);
        expected.ShouldBe([.. expected.OrderBy(t => t.Amount)], "Cheapest first by default.");
        html.ShouldContain("pour 4 jours");
        html.ShouldContain("équivalent indicatif");
        html.ShouldContain("name=\"__RequestVerificationToken\"");
    }

    [Fact]
    public async Task The_category_of_a_deep_link_is_highlighted_first()
    {
        var (_, html) = await GetAsync($"/fr/reserver?lieu=TUN&depart={LocalDate(21)}&retour={LocalDate(25)}&cat=ECMR&src=whatsapp");

        OfferCodes(html)[0].ShouldBe("ECMR");
        html.ShouldContain("<li class=\"offer offer--highlighted\"><article class=\"offer__card\" aria-labelledby=\"offer-ECMR\">");
        html.ShouldContain("Votre sélection");
        html.ShouldContain("<input type=\"hidden\" name=\"src\" value=\"whatsapp\" />");
    }

    [Fact]
    public async Task Filters_keep_only_the_matching_categories()
    {
        var (_, automatic) = await GetAsync($"/fr/reserver?lieu=TUN&depart={LocalDate(22)}&retour={LocalDate(26)}&boite=auto");
        var (_, fiveSeats) = await GetAsync($"/fr/reserver?lieu=TUN&depart={LocalDate(22)}&retour={LocalDate(26)}&places=5&tri=prix_desc");

        OfferCodes(automatic).ShouldBe(["EDAR"]);
        OfferCodes(fiveSeats).ShouldNotContain("MCMR");
        OfferCodes(fiveSeats).Count.ShouldBeGreaterThan(1);
        var totals = OfferTotals(fiveSeats).Select(t => decimal.Parse(t.Replace(" TND", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal), NumberStyles.Number, French)).ToList();
        totals.ShouldBe([.. totals.OrderByDescending(t => t)]);
        fiveSeats.ShouldContain("<option value=\"prix_desc\" selected>");
    }

    [Fact]
    public async Task The_english_results_live_at_book_and_the_french_slug_redirects()
    {
        var query = $"?lieu=TUN&depart={LocalDate(23)}&retour={LocalDate(27)}";
        var (status, html) = await GetAsync("/en/book" + query);
        using var client = host.CreateClient();
        var wrongSlug = await client.GetAsync(new Uri("/en/reserver" + query, UriKind.Relative), Ct);

        status.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("<html lang=\"en\"");
        html.ShouldContain("Available cars");
        html.ShouldContain("Total incl. VAT");
        wrongSlug.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Uri.UnescapeDataString(Support.HttpSignIn.PathAndQuery(wrongSlug.Headers.Location!)).ShouldBe("/en/book" + query);
    }

    [Theory]
    [InlineData("?lieu=TUN&depart=2020-07-01T10:00&retour=2020-07-05T10:00", "La date de départ est passée")]
    [InlineData("?lieu=XYZ&depart={0}&retour={1}", "Ce lieu de départ n'existe pas")]
    [InlineData("?lieu=TUN&depart={1}&retour={0}", "Le retour doit être après le départ.")]
    [InlineData("?lieu=TUN&depart=demain&retour={1}", "La date de départ n'est pas valide")]
    public async Task Invalid_searches_are_explained_with_a_summary_and_field_messages(string queryTemplate, string message)
    {
        var url = "/fr/reserver" + string.Format(CultureInfo.InvariantCulture, queryTemplate, LocalDate(30), LocalDate(34));

        var (status, html) = await GetAsync(url);

        status.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("<title>Erreur : Rechercher une voiture | CarRental</title>");
        html.ShouldContain("class=\"error-summary\" role=\"alert\"");
        html.ShouldContain(message);
        html.ShouldContain("aria-invalid=\"true\"");
        html.ShouldContain("class=\"field-error\"");
        OfferCodes(html).ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_any_search_the_form_is_shown_without_errors()
    {
        var (status, html) = await GetAsync("/fr/reserver");

        status.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Indiquez votre lieu et vos dates");
        html.ShouldNotContain("error-summary");
        html.ShouldContain("<meta name=\"robots\" content=\"noindex, follow\"");
    }
}
