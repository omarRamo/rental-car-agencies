using System.Net;
using System.Xml.Linq;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Public.PublicTestData;

namespace CarRental.Integration.Tests.Public;

/// <summary>
/// One site per agency, at the address of the agency (ADR-0067): <c>/{agency}/{culture}/…</c> shows that agency only,
/// an unknown address, a suspended agency or a site whose module is off answer 404, <c>/{agency}</c> leads to the best
/// culture, the root is the page of the platform, the sitemap lists the open sites and the WhatsApp button follows the
/// module of the agency.
/// </summary>
public sealed class AgencySitesTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_site_shows_the_name_the_contact_and_the_locations_of_its_own_agency_only()
    {
        using var client = host.CreateClient();

        var tunis = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri($"/{DemoSlug}/fr/agence", UriKind.Relative), Ct));
        var sousse = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri($"/{SecondDemoSlug}/fr/agence", UriKind.Relative), Ct));

        tunis.ShouldContain("<a class=\"site-brand\" href=\"/agence-demo-tunis/fr\">Agence Démo Tunis</a>");
        tunis.ShouldContain("Aéroport Tunis-Carthage");
        tunis.ShouldContain("tel:+21670000000");
        tunis.ShouldNotContain("Agence Sousse Centre");
        tunis.ShouldNotContain("Agence Démo Sousse");
        sousse.ShouldContain("<a class=\"site-brand\" href=\"/agence-demo-sousse/fr\">Agence Démo Sousse</a>");
        sousse.ShouldContain("Agence Sousse Centre");
        sousse.ShouldContain("Aéroport Monastir Habib-Bourguiba");
        sousse.ShouldContain("tel:+21673000000");
        sousse.ShouldContain("<link rel=\"canonical\" href=\"http://localhost/agence-demo-sousse/fr/agence\" />");
        sousse.ShouldNotContain("Aéroport Tunis-Carthage");
        sousse.ShouldNotContain("Agence Démo Tunis");
        sousse.ShouldNotContain("tel:+21670000000");
    }

    [Fact]
    public async Task Each_site_searches_and_prices_the_fleet_of_its_own_agency_only()
    {
        using var client = host.CreateClient();
        var dates = $"depart={LocalDate(140)}&retour={LocalDate(143)}";

        var tunis = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri($"/{DemoSlug}/fr/reserver?lieu=TUN&{dates}", UriKind.Relative), Ct));
        var sousse = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri($"/{SecondDemoSlug}/fr/reserver?lieu=SOU&{dates}", UriKind.Relative), Ct));
        var elsewhere = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri($"/{SecondDemoSlug}/fr/reserver?lieu=TUN&{dates}", UriKind.Relative), Ct));

        OfferCodes(tunis).ShouldContain(code => code != "ECMR" && code != "CDMR", "the Tunis agency has categories the Sousse agency has not");
        OfferCodes(sousse).ShouldNotBeEmpty();
        OfferCodes(sousse).ShouldBeSubsetOf(["ECMR", "CDMR"], "the categories of the Sousse agency only");
        sousse.ShouldContain($"action=\"/{SecondDemoSlug}/fr/reserver/selection\"");
        sousse.ShouldNotContain("Aéroport Tunis-Carthage");
        elsewhere.ShouldContain("Ce lieu de départ n'existe pas", customMessage: "a location of another agency is unknown to this site");
        OfferCodes(elsewhere).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_public_api_of_a_site_answers_for_its_agency_only()
    {
        using var client = host.CreateClient();
        var dates = $"depart={LocalDate(141)}&retour={LocalDate(144)}";

        var own = await client.GetAsync(new Uri($"/{SecondDemoSlug}/api/public/availability?lieu=SOU&{dates}", UriKind.Relative), Ct);
        var other = await client.GetAsync(new Uri($"/{SecondDemoSlug}/api/public/availability?lieu=TUN&{dates}", UriKind.Relative), Ct);
        var unknown = await client.GetAsync(new Uri($"/agence-inconnue-{Guid.NewGuid():N}/api/public/availability?lieu=SOU&{dates}", UriKind.Relative), Ct);

        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await own.Content.ReadAsStringAsync(Ct)).ShouldContain("\"code\":\"SOU\"");
        other.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a location of another agency is unknown to this site");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_suspended_or_closed_site_is_not_found_and_reopens_with_its_agency()
    {
        var agency = await TestAgencies.RegisterAsync(host, "vitrine", Ct);
        using var client = host.CreateClient();

        (await StatusAsync(client, $"/{agency.Slug}/fr")).ShouldBe(HttpStatusCode.OK);
        (await StatusAsync(client, $"/{agency.Slug}/en/faq")).ShouldBe(HttpStatusCode.OK);

        await TestAgencies.SetModulesAsync(host, agency.Id, agency.Modules & ~AgencyModules.PublicSite, Ct);
        var closed = await client.GetAsync(new Uri($"/{agency.Slug}/fr", UriKind.Relative), Ct);
        closed.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the public site module is off");
        WebUtility.HtmlDecode(await closed.Content.ReadAsStringAsync(Ct)).ShouldContain("Page introuvable", customMessage: "the status code page renders");
        (await StatusAsync(client, $"/{agency.Slug}")).ShouldBe(HttpStatusCode.NotFound);
        (await StatusAsync(client, $"/{agency.Slug}/fr/reserver")).ShouldBe(HttpStatusCode.NotFound);

        await TestAgencies.SetModulesAsync(host, agency.Id, agency.Modules, Ct);
        (await StatusAsync(client, $"/{agency.Slug}/fr")).ShouldBe(HttpStatusCode.OK, "the site reopens with its module");

        await TestAgencies.SetActiveAsync(host, agency.Id, active: false, Ct);
        (await StatusAsync(client, $"/{agency.Slug}/fr/agence")).ShouldBe(HttpStatusCode.NotFound, "a suspended agency has no site");
        await TestAgencies.SetActiveAsync(host, agency.Id, active: true, Ct);
        (await StatusAsync(client, $"/{agency.Slug}/fr/agence")).ShouldBe(HttpStatusCode.OK);

        (await StatusAsync(client, $"/agence-inconnue-{Guid.NewGuid():N}/fr")).ShouldBe(HttpStatusCode.NotFound);
        (await StatusAsync(client, "/admin-fr/fr")).ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_closed_or_suspended_site_leaves_the_sitemap()
    {
        var agency = await TestAgencies.RegisterAsync(host, "plan", Ct);
        using var client = host.CreateClient();

        (await SitemapAsync(client)).ShouldContain($"http://localhost/{agency.Slug}/fr/agence");

        await TestAgencies.SetActiveAsync(host, agency.Id, active: false, Ct);
        (await SitemapAsync(client)).ShouldNotContain($"http://localhost/{agency.Slug}/fr/agence");

        await TestAgencies.SetActiveAsync(host, agency.Id, active: true, Ct);
        await TestAgencies.SetModulesAsync(host, agency.Id, agency.Modules & ~AgencyModules.PublicSite, Ct);
        (await SitemapAsync(client)).ShouldNotContain($"http://localhost/{agency.Slug}/fr/agence");
        (await SitemapAsync(client)).ShouldContain("http://localhost/agence-demo-tunis/fr/agence");
    }

    [Fact]
    public async Task An_address_in_another_case_leads_to_the_canonical_address_of_the_site()
    {
        using var client = host.CreateClient();

        var upper = await client.GetAsync(new Uri("/AGENCE-DEMO-TUNIS/fr/agence?x=1", UriKind.Relative), Ct);

        upper.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Support.HttpSignIn.PathAndQuery(upper.Headers.Location!).ShouldBe("/agence-demo-tunis/fr/agence?x=1");
    }

    [Fact]
    public async Task The_whatsapp_button_shows_on_the_sites_whose_agency_has_the_module()
    {
        var agency = await TestAgencies.RegisterAsync(host, "whatsapp", Ct);
        using var client = host.CreateClient();

        (await client.GetStringAsync(new Uri($"/{agency.Slug}/fr", UriKind.Relative), Ct)).ShouldNotContain("whatsapp__button", customMessage: "the module is off for a new agency");

        await TestAgencies.SetModulesAsync(host, agency.Id, agency.Modules | AgencyModules.WhatsApp, Ct);
        var home = await client.GetStringAsync(new Uri($"/{agency.Slug}/fr", UriKind.Relative), Ct);
        home.ShouldContain("whatsapp__button");
        home.ShouldContain("https://wa.me/21600000000");
    }

    [Fact]
    public async Task The_root_is_the_page_of_the_platform_without_any_agency()
    {
        using var client = host.CreateClient();
        using var english = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        english.Headers.Add("Accept-Language", "en-GB,en;q=0.8");

        var french = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);
        var englishPage = await client.SendAsync(english, Ct);

        french.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await french.Content.ReadAsStringAsync(Ct));
        html.ShouldContain("<html lang=\"fr\" dir=\"ltr\">");
        html.ShouldContain("La location de voitures, agence par agence");
        html.ShouldContain("href=\"/admin\"");
        html.ShouldNotContain("Agence Démo Tunis", customMessage: "the platform lists no agency");
        html.ShouldNotContain("Agence Démo Sousse");
        html.ShouldNotContain("whatsapp__button");
        html.ShouldNotContain("MudBlazor");
        french.Headers.Vary.ShouldContain("Accept-Language");
        var englishHtml = WebUtility.HtmlDecode(await englishPage.Content.ReadAsStringAsync(Ct));
        englishHtml.ShouldContain("<html lang=\"en\" dir=\"ltr\">");
        englishHtml.ShouldContain("Car rental, agency by agency");
        englishHtml.ShouldContain("href=\"/culture/set?culture=fr&returnUrl=%2F\"");
    }

    [Fact]
    public async Task The_visitor_of_a_site_works_for_the_agency_of_its_address()
    {
        using var client = host.CreateClient();
        var directory = host.Factory.Services.GetRequiredService<IAgencyDirectory>();
        var sousse = (await directory.FindBySlugAsync(SecondDemoSlug, Ct)).ShouldNotBeNull();
        var tunis = (await directory.FindBySlugAsync(DemoSlug, Ct)).ShouldNotBeNull();

        var faq = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri($"/{SecondDemoSlug}/fr/faq", UriKind.Relative), Ct));

        sousse.HasOpenPublicSite.ShouldBeTrue();
        tunis.Id.ShouldNotBe(sousse.Id);
        faq.ShouldContain("Agence Sousse Centre", customMessage: "the FAQ quotes the locations of the agency of the site");
        faq.ShouldNotContain("Aéroport Tunis-Carthage");
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string path) =>
        (await client.GetAsync(new Uri(path, UriKind.Relative), Ct)).StatusCode;

    private static async Task<IReadOnlyList<string>> SitemapAsync(HttpClient client)
    {
        var xml = await client.GetStringAsync(new Uri("/sitemap.xml", UriKind.Relative), Ct);
        return [.. XDocument.Parse(xml).Descendants(XName.Get("loc", "http://www.sitemaps.org/schemas/sitemap/0.9")).Select(e => e.Value)];
    }
}
