using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;
using CarRental.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Public.PublicTestData;

namespace CarRental.Integration.Tests.Public;

/// <summary>Caching headers, SEO documents and head tags, the "Choose" action and the public availability API over HTTP.</summary>
public sealed class PublicHttpTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/fr")]
    [InlineData("/en")]
    [InlineData("/fr/agence")]
    [InlineData("/en/agency")]
    [InlineData("/fr/faq")]
    [InlineData("/fr/conditions")]
    [InlineData("/en/privacy")]
    public async Task Pages_without_post_form_are_cacheable_and_set_no_cookie(string path)
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.Public.ShouldBeTrue();
        response.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromSeconds(300));
        response.Headers.CacheControl.NoStore.ShouldBeFalse();
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();
        response.Headers.Contains("Pragma").ShouldBeFalse();
        html.ShouldNotContain("__RequestVerificationToken");
        html.ShouldNotContain("_framework/blazor.web");
    }

    [Fact]
    public async Task The_results_page_keeps_its_antiforgery_token_and_is_never_stored()
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri($"/fr/reserver?lieu=TUN&depart={LocalDate(40)}&retour={LocalDate(43)}", UriKind.Relative), Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith("carrental.af=", StringComparison.Ordinal));
        HttpSignIn.AntiforgeryToken(html).ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_signed_in_staff_member_does_not_get_a_publicly_cacheable_copy()
    {
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, Testing.TestAccounts.AgentEmail, "/admin", Ct);

        var response = await client.GetAsync(new Uri("/fr/faq", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (response.Headers.CacheControl?.Public ?? false).ShouldBeFalse();
    }

    [Fact]
    public async Task Pages_publish_their_title_description_canonical_alternates_and_open_graph()
    {
        using var client = host.CreateClient();

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri("/en/agency", UriKind.Relative), Ct));

        html.ShouldContain("<title>Agency and contact | CarRental</title>");
        html.ShouldContain("<meta name=\"description\" content=\"Address, opening hours, phone, e-mail and WhatsApp of your car rental agency in Tunis.\" />");
        html.ShouldContain("<link rel=\"canonical\" href=\"http://localhost/en/agency\" />");
        html.ShouldContain("<link rel=\"alternate\" hreflang=\"fr\" href=\"http://localhost/fr/agence\" />");
        html.ShouldContain("<link rel=\"alternate\" hreflang=\"en\" href=\"http://localhost/en/agency\" />");
        html.ShouldContain("<link rel=\"alternate\" hreflang=\"x-default\" href=\"http://localhost/fr/agence\" />");
        html.ShouldContain("<meta property=\"og:url\" content=\"http://localhost/en/agency\" />");
        html.ShouldContain("<meta property=\"og:locale\" content=\"en_GB\" />");
        html.ShouldContain("<script type=\"application/ld+json\">{\"@context\":\"https://schema.org\",\"@type\":\"AutoRental\"");
        html.ShouldContain("\"latitude\":36.7998");
        html.ShouldContain("Every day, 08:00 to 20:00");
        html.ShouldContain("tel:+21670000000");
    }

    [Fact]
    public async Task The_sitemap_and_robots_are_served_with_absolute_addresses()
    {
        using var client = host.CreateClient();

        var sitemap = await client.GetAsync(new Uri("/sitemap.xml", UriKind.Relative), Ct);
        var robots = await client.GetAsync(new Uri("/robots.txt", UriKind.Relative), Ct);

        sitemap.Content.Headers.ContentType!.MediaType.ShouldBe("application/xml");
        sitemap.Headers.CacheControl!.MaxAge.ShouldBe(TimeSpan.FromHours(1));
        var locations = XDocument.Parse(await sitemap.Content.ReadAsStringAsync(Ct)).Descendants(XName.Get("loc", "http://www.sitemaps.org/schemas/sitemap/0.9")).Select(e => e.Value).ToList();
        locations.Count.ShouldBe(10);
        locations.ShouldContain("http://localhost/en/terms");
        robots.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        (await robots.Content.ReadAsStringAsync(Ct)).ShouldBe("User-agent: *\nDisallow: /\n", "The test environment is not production: nothing is indexed.");
    }

    [Fact]
    public async Task Choosing_a_category_needs_the_antiforgery_token_and_brings_back_the_results_with_a_notice()
    {
        using var client = host.CreateClient();
        var search = $"lieu=TUN&depart={LocalDate(45)}&retour={LocalDate(48)}&src=whatsapp&utm_campaign=ete";
        var page = await client.GetStringAsync(new Uri($"/fr/reserver?{search}", UriKind.Relative), Ct);
        var fields = new Dictionary<string, string>
        {
            ["lieu"] = "TUN",
            ["depart"] = LocalDate(45),
            ["retour"] = LocalDate(48),
            ["cat"] = "ECMR",
            ["src"] = "whatsapp",
            ["utm_campaign"] = "ete",
        };

        using var forged = new FormUrlEncodedContent(fields);
        var refused = await client.PostAsync(new Uri("/fr/reserver/selection", UriKind.Relative), forged, Ct);
        using var valid = new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = HttpSignIn.AntiforgeryToken(page) });
        var accepted = await client.PostAsync(new Uri("/fr/reserver/selection", UriKind.Relative), valid, Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        accepted.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var location = Uri.UnescapeDataString(HttpSignIn.PathAndQuery(accepted.Headers.Location!));
        location.ShouldBe($"/fr/reserver?lieu=TUN&depart={LocalDate(45)}&retour={LocalDate(48)}&cat=ECMR&src=whatsapp&utm_campaign=ete&info=bientot");
        var back = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri(location, UriKind.Relative), Ct));
        back.ShouldContain("La réservation en ligne ouvre bientôt");
        OfferCodes(back)[0].ShouldBe("ECMR");
    }

    [Fact]
    public async Task The_availability_api_returns_the_categories_with_their_remaining_count_and_total()
    {
        var (agency, locations, categories) = await DemoAgencyAsync(host, Ct);
        var (pickup, dropOff) = (LocalDate(50), LocalDate(53));
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri($"/api/public/availability?lieu=NBE&retour_lieu=TUN&depart={pickup}&retour={dropOff}&src=whatsapp", UriKind.Relative), Ct);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        json.GetProperty("pickupLocation").GetProperty("code").GetString().ShouldBe("NBE");
        json.GetProperty("pickupLocation").GetProperty("kind").GetString().ShouldBe("airport");
        json.GetProperty("returnLocation").GetProperty("code").GetString().ShouldBe("TUN");
        json.GetProperty("pickupAt").GetDateTimeOffset().ShouldBe(Instant(pickup));
        json.GetProperty("pickupAt").GetString()!.ShouldEndWith("+01:00");
        json.GetProperty("channel").GetString().ShouldBe("whatsapp");
        json.GetProperty("currency").GetString().ShouldBe("TND");
        json.GetProperty("requiredMinimumDays").ValueKind.ShouldBe(JsonValueKind.Null);
        var offers = json.GetProperty("categories").EnumerateArray().ToList();
        offers.ShouldNotBeEmpty();

        await using var scope = host.CreateScope();
        var pricing = scope.ServiceProvider.GetRequiredService<IPricingService>();
        foreach (var offer in offers)
        {
            var category = categories.Single(c => c.AcrissCode == offer.GetProperty("code").GetString());
            var quote = (await pricing.QuoteAsync(
                new PriceRequest(agency.Id, category.Id, locations.Single(l => l.Code == "NBE").Id, locations.Single(l => l.Code == "TUN").Id, Instant(pickup), Instant(dropOff), SalesChannel.WhatsApp),
                Ct)).Value;
            offer.GetProperty("totalInclVat").GetDecimal().ShouldBe(quote.TotalInclVat.Amount);
            offer.GetProperty("days").GetInt32().ShouldBe(quote.Days);
            offer.GetProperty("deposit").GetDecimal().ShouldBe(category.Deposit.Amount);
            offer.GetProperty("remaining").GetInt32().ShouldBeGreaterThan(0);
            offer.GetProperty("seats").GetInt32().ShouldBe(category.Seats);
            offer.GetProperty("eurEquivalent").GetDecimal().ShouldBe(Math.Round(quote.TotalInclVat.Amount / 3.40m, 0, MidpointRounding.AwayFromZero));
        }
    }

    [Fact]
    public async Task The_availability_api_reports_invalid_searches_as_a_validation_problem()
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri("/api/public/availability?lieu=XYZ&depart=2020-01-01T10:00", UriKind.Relative), Ct);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var errors = json.GetProperty("errors");
        errors.GetProperty("lieu")[0].GetString().ShouldBe("pickup_location_unknown");
        errors.GetProperty("depart")[0].GetString().ShouldBe("pickup_past");
        errors.GetProperty("retour")[0].GetString().ShouldBe("return_required");
    }

    [Fact]
    public async Task The_availability_api_applies_the_filters()
    {
        using var client = host.CreateClient();

        var json = JsonDocument.Parse(await client.GetStringAsync(
            new Uri($"/api/public/availability?lieu=TUN&depart={LocalDate(55)}&retour={LocalDate(58)}&boite=auto", UriKind.Relative),
            Ct)).RootElement;

        json.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("transmission").GetString()).ShouldAllBe(t => t == "automatic");
        json.GetProperty("channel").GetString().ShouldBe("web");
    }
}
