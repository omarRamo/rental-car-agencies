using System.Net;
using System.Xml.Linq;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Referential.Platform;
using CarRental.Domain.Referential;
using CarRental.Integration.Tests.Support;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Endpoints;
using CarRental.Web.Endpoints.PublicSite;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Public.PublicTestData;

namespace CarRental.Integration.Tests.Public;

/// <summary>
/// The site of an agency on the agency's own host names (ADR-0069): the support attaches host names to an agency from
/// the platform pages, the site then answers on them without the address segment (<c>/fr/…</c>), its links, forms,
/// canonical addresses and sitemap follow, the secondary hosts redirect to the primary one, the platform's own addresses
/// are left alone, and the platform host keeps serving <c>/{agency}/fr</c>.
/// </summary>
public sealed class AgencyDomainsTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_support_attaches_host_names_to_an_agency_the_first_one_being_primary()
    {
        var agency = await TestAgencies.RegisterAsync(host, "domaines", Ct);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var support = await TestAgencies.SupportAsync(host);
        await using var scope = TestAgencies.PlatformScope(host, support);
        var platform = scope.ServiceProvider.GetRequiredService<AgencyPlatformService>();

        var first = await platform.AddDomainAsync(agency.Id, $" WWW.{suffix}.Example.TEST ", Ct);
        var second = await platform.AddDomainAsync(agency.Id, $"{suffix}.example.test", Ct);
        var taken = await platform.AddDomainAsync(agency.Id, $"www.{suffix}.example.test", Ct);
        var invalid = await platform.AddDomainAsync(agency.Id, "https://agence.tn", Ct);
        var platformHost = await platform.AddDomainAsync(agency.Id, "localhost", Ct);

        first.IsSuccess.ShouldBeTrue(first.IsFailure ? first.Error.ToString() : null);
        second.IsSuccess.ShouldBeTrue(second.IsFailure ? second.Error.ToString() : null);
        taken.Error.ShouldBe(ReferentialErrors.AgencyDomainTaken);
        invalid.Error.ShouldBe(ReferentialErrors.AgencyDomainInvalid);
        platformHost.Error.ShouldBe(ReferentialErrors.AgencyDomainPlatformHost, "the platform's own host names never serve an agency");

        var domains = (await platform.ListDomainsAsync(agency.Id, Ct)).Value;
        domains.Select(d => (d.HostName, d.IsPrimary)).ShouldBe([($"www.{suffix}.example.test", true), ($"{suffix}.example.test", false)]);

        // Another agency cannot take a host of this one; the directory knows the hosts at once.
        var other = await TestAgencies.RegisterAsync(host, "domaines-bis", Ct);
        (await platform.AddDomainAsync(other.Id, $"{suffix}.example.test", Ct)).Error.ShouldBe(ReferentialErrors.AgencyDomainTaken);
        var directory = host.Factory.Services.GetRequiredService<IAgencyDirectory>();
        var found = (await directory.FindByHostAsync($"WWW.{suffix}.example.test", Ct)).ShouldNotBeNull();
        found.Id.ShouldBe(agency.Id);
        found.PrimaryHost.ShouldBe($"www.{suffix}.example.test");
        found.Hosts.ShouldBe([$"www.{suffix}.example.test", $"{suffix}.example.test"]);
        found.LinkSlug.ShouldBe(string.Empty);

        // The primary host changes; removing the primary one hands the role to the oldest remaining host.
        (await platform.SetPrimaryDomainAsync(agency.Id, domains[1].Id, Ct)).IsSuccess.ShouldBeTrue();
        (await directory.FindAsync(agency.Id, Ct))!.PrimaryHost.ShouldBe($"{suffix}.example.test");
        (await platform.RemoveDomainAsync(agency.Id, domains[1].Id, Ct)).IsSuccess.ShouldBeTrue();
        (await directory.FindAsync(agency.Id, Ct))!.PrimaryHost.ShouldBe($"www.{suffix}.example.test");
        (await platform.RemoveDomainAsync(agency.Id, domains[1].Id, Ct)).Error.ShouldBe(ReferentialErrors.AgencyDomainNotFound);
        (await platform.RemoveDomainAsync(agency.Id, domains[0].Id, Ct)).IsSuccess.ShouldBeTrue();
        (await directory.FindAsync(agency.Id, Ct))!.PrimaryHost.ShouldBeNull();
        (await directory.FindByHostAsync($"www.{suffix}.example.test", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_manager_cannot_manage_the_host_names_of_an_agency()
    {
        var agency = await TestAgencies.RegisterAsync(host, "domaines-gerant", Ct);
        await using var scope = host.CreateScope(Testing.TestActors.Staff(agency.Id, Application.Identity.Contracts.Roles.Manager));

        var refused = await scope.ServiceProvider.GetRequiredService<AgencyPlatformService>().AddDomainAsync(agency.Id, $"{Guid.NewGuid():N}.example.test", Ct);

        refused.Error.ShouldBe(PlatformErrors.Forbidden);
    }

    [Fact]
    public async Task The_site_answers_on_its_own_host_without_the_address_segment()
    {
        // An agency of the test with vehicles and a published plan: its results page offers categories to choose.
        var world = await Bookings.BookingWorld.CreateAsync(host, Ct);
        var agency = (await host.Factory.Services.GetRequiredService<IAgencyDirectory>().FindAsync(world.AgencyId, Ct)).ShouldNotBeNull();
        var (primary, secondary) = await AttachHostsAsync(agency.Id);
        string office;
        await using (var scope = host.CreateScope())
        {
            office = (await scope.ServiceProvider.GetRequiredService<IReferentialQueries>().GetLocationsAsync(agency.Id, Ct)).Single(l => l.Id == world.OfficeId).Code;
        }

        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Host = primary;

        var home = await client.GetAsync(new Uri("/fr", UriKind.Relative), Ct);
        var html = WebUtility.HtmlDecode(await home.Content.ReadAsStringAsync(Ct));
        var english = await client.GetAsync(new Uri("/en/agency", UriKind.Relative), Ct);
        var englishHtml = WebUtility.HtmlDecode(await english.Content.ReadAsStringAsync(Ct));
        var root = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);
        var results = await client.GetAsync(new Uri($"/fr/reserver?lieu={office}&depart={LocalDate(160)}&retour={LocalDate(163)}", UriKind.Relative), Ct);
        var resultsHtml = WebUtility.HtmlDecode(await results.Content.ReadAsStringAsync(Ct));

        home.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain($"<a class=\"site-brand\" href=\"/fr\">{agency.Name}</a>");
        html.ShouldContain("href=\"/fr/agence\"");
        html.ShouldContain("href=\"/fr/faq\"");
        html.ShouldContain($"<link rel=\"canonical\" href=\"http://{primary}/fr\" />");
        html.ShouldContain($"hreflang=\"en\" href=\"http://{primary}/en\"");
        html.ShouldContain($"hreflang=\"x-default\" href=\"http://{primary}/\"");
        html.ShouldContain("action=\"/fr/reserver\"", customMessage: "the search form posts to the site of the host");
        html.ShouldContain("href=\"/en\"", customMessage: "the language switcher stays on the host");
        html.ShouldNotContain($"/{agency.Slug}/", customMessage: "no address segment on the agency's own host");
        english.StatusCode.ShouldBe(HttpStatusCode.OK);
        englishHtml.ShouldContain($"<link rel=\"canonical\" href=\"http://{primary}/en/agency\" />");
        englishHtml.ShouldContain("href=\"/fr/agence\"", customMessage: "the language switcher translates the slug-less address");
        root.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        root.Headers.Location!.ToString().ShouldBe("/fr");
        results.StatusCode.ShouldBe(HttpStatusCode.OK);
        OfferCodes(resultsHtml).ShouldNotBeEmpty("the fleet of the agency is offered on its own host");
        resultsHtml.ShouldContain("action=\"/fr/reserver/selection\"", customMessage: "the Choose forms post to the site of the host");
        resultsHtml.ShouldContain("action=\"/fr/reserver\"", customMessage: "the filters form too");
        resultsHtml.ShouldNotContain($"/{agency.Slug}/");
        results.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith("carrental.af=", StringComparison.Ordinal));

        // The platform host still serves the same site at its address, with the canonical address on the agency's host.
        using var platform = host.CreateClient();
        var onPlatform = WebUtility.HtmlDecode(await platform.GetStringAsync(new Uri($"/{agency.Slug}/fr/agence", UriKind.Relative), Ct));
        onPlatform.ShouldContain($"<a class=\"site-brand\" href=\"/{agency.Slug}/fr\">{agency.Name}</a>");
        onPlatform.ShouldContain($"<link rel=\"canonical\" href=\"http://{primary}/fr/agence\" />");
        secondary.ShouldNotBe(primary);
    }

    [Fact]
    public async Task A_secondary_host_redirects_to_the_primary_one_and_the_platform_addresses_are_left_alone()
    {
        var (agency, primary, secondary) = await AgencyWithHostsAsync("redirection-domaine");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Host = secondary;

        var redirected = await client.GetAsync(new Uri("/fr/agence?x=1", UriKind.Relative), Ct);
        var admin = await client.GetAsync(new Uri("/admin", UriKind.Relative), Ct);
        var health = await client.GetAsync(new Uri("/health", UriKind.Relative), Ct);
        var otherSite = await client.GetAsync(new Uri($"/{DemoSlug}/fr", UriKind.Relative), Ct);

        redirected.StatusCode.ShouldBe(HttpStatusCode.MovedPermanently);
        redirected.Headers.Location!.ToString().ShouldBe($"http://{primary}/fr/agence?x=1");
        admin.StatusCode.ShouldBe(HttpStatusCode.Redirect, "the back office is the platform's: a sign-in is asked, not a page of the site");
        admin.Headers.Location!.ToString().ShouldContain("/account/login");
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        otherSite.StatusCode.ShouldBe(HttpStatusCode.MovedPermanently, "a secondary host redirects every address of the site");

        client.DefaultRequestHeaders.Host = primary;
        var foreign = await client.GetAsync(new Uri($"/{DemoSlug}/fr", UriKind.Relative), Ct);
        var own = await client.GetAsync(new Uri($"/{agency.Slug}/fr", UriKind.Relative), Ct);
        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound, "another agency's address is no page of this host");
        own.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the address segment is not part of the addresses of the agency's own host");
    }

    [Fact]
    public async Task A_closed_site_answers_not_found_on_its_own_host_and_reopens()
    {
        var (agency, primary, _) = await AgencyWithHostsAsync("ferme-domaine");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Host = primary;

        await TestAgencies.SetActiveAsync(host, agency.Id, active: false, Ct);
        var suspended = await client.GetAsync(new Uri("/fr", UriKind.Relative), Ct);
        await TestAgencies.SetActiveAsync(host, agency.Id, active: true, Ct);
        var open = await client.GetAsync(new Uri("/fr", UriKind.Relative), Ct);

        suspended.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        WebUtility.HtmlDecode(await suspended.Content.ReadAsStringAsync(Ct)).ShouldContain("Page introuvable", customMessage: "the status code page renders");
        open.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_sitemap_and_robots_follow_the_host()
    {
        var (agency, primary, _) = await AgencyWithHostsAsync("plan-domaine");
        using var client = host.CreateClient();

        var platformSitemap = await SitemapAsync(client);
        client.DefaultRequestHeaders.Host = primary;
        var ownSitemap = await SitemapAsync(client);
        var robots = await client.GetAsync(new Uri("/robots.txt", UriKind.Relative), Ct);

        platformSitemap.ShouldContain($"http://{primary}/fr/agence", "the canonical address of the site is on its own host");
        platformSitemap.ShouldNotContain($"http://localhost/{agency.Slug}/fr/agence");
        platformSitemap.ShouldContain($"http://localhost/{DemoSlug}/fr/agence");
        ownSitemap.ShouldContain($"http://{primary}/fr/agence");
        ownSitemap.ShouldContain($"http://{primary}/en/agency");
        ownSitemap.ShouldNotContain($"http://localhost/{DemoSlug}/fr/agence", customMessage: "the sitemap of a host lists that site only");
        robots.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Outside production the test host keeps every robot out; the rules of an indexable own host are checked directly.
        var indexable = SeoEndpoints.Robots(new Uri($"https://{primary}/"), ["fr", "en"], allowIndexing: true, ownHost: true);
        indexable.ShouldContain("Disallow: /fr/reserver\n");
        indexable.ShouldContain("Disallow: /en/my-booking\n");
        indexable.ShouldNotContain("Disallow: /*/fr/reserver");
        indexable.ShouldContain($"Sitemap: https://{primary}/sitemap.xml");
        SeoEndpoints.Robots(new Uri("https://rental.app/"), ["fr"], allowIndexing: true).ShouldContain("Disallow: /*/fr/reserver\n");
    }

    [Fact]
    public async Task The_return_of_the_payment_gateway_goes_back_to_the_agency_s_host()
    {
        var (agency, primary, _) = await AgencyWithHostsAsync("retour-paiement");
        var directory = host.Factory.Services.GetRequiredService<IAgencyDirectory>();

        var bounced = await ClicToPayEndpoints.OwnHostAddressAsync($"/{agency.Slug}/fr/reserver/confirmation/LOC-2027-000001", "https", directory, Ct);
        var withQuery = await ClicToPayEndpoints.OwnHostAddressAsync($"/{agency.Slug}/fr/ma-reservation/dossier?paiement=1", "https", directory, Ct);
        var platformSite = await ClicToPayEndpoints.OwnHostAddressAsync($"/{DemoSlug}/fr/reserver/confirmation/LOC-2027-000001", "https", directory, Ct);

        bounced.ShouldBe($"https://{primary}/fr/reserver/confirmation/LOC-2027-000001");
        withQuery.ShouldBe($"https://{primary}/fr/ma-reservation/dossier?paiement=1");
        platformSite.ShouldBeNull("a site without own host keeps the platform address");
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/fr", true)]
    [InlineData("/en/book/abc/drivers", true)]
    [InlineData("/api/public/availability", true)]
    [InlineData("/api/v1/site", true)]
    [InlineData("/api/channels/booking/inbound", false)]
    [InlineData("/api/openapi/v1.json", false)]
    [InlineData("/admin", false)]
    [InlineData("/account/login", false)]
    [InlineData("/health/ready", false)]
    [InlineData("/webhooks/whatsapp", false)]
    [InlineData("/payments/clictopay/return", false)]
    [InlineData("/media/categories/1", false)]
    [InlineData("/_framework/blazor.web.js", false)]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css", false)]
    [InlineData("/css/site.css", false)]
    [InlineData("/js/search-form.js", false)]
    [InlineData("/favicon.svg", false)]
    [InlineData("/sitemap.xml", false)]
    [InlineData("/robots.txt", false)]
    [InlineData("/culture/set", false)]
    [InlineData("/status/404", false)]
    [InlineData("/dev/clictopay/start", false)]
    public void On_an_agency_host_only_the_addresses_of_the_site_are_rewritten(string path, bool rewritten) =>
        AgencyHostMiddleware.IsSiteAddress(new PathString(path)).ShouldBe(rewritten);

    /// <summary>A new agency with a primary and a secondary host of the test (unique names), as the directory sees it.</summary>
    private async Task<(AgencySummary Agency, string Primary, string Secondary)> AgencyWithHostsAsync(string label)
    {
        var agency = await TestAgencies.RegisterAsync(host, label, Ct);
        var (primary, secondary) = await AttachHostsAsync(agency.Id);
        return (agency, primary, secondary);
    }

    /// <summary>Attaches a primary and a secondary host of the test (unique names) to the agency, as the support does.</summary>
    private async Task<(string Primary, string Secondary)> AttachHostsAsync(Guid agencyId)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var primary = $"www.{suffix}.example.test";
        var secondary = $"{suffix}.example.test";
        await using var scope = TestAgencies.PlatformScope(host, await TestAgencies.SupportAsync(host));
        var platform = scope.ServiceProvider.GetRequiredService<AgencyPlatformService>();
        (await platform.AddDomainAsync(agencyId, primary, Ct)).IsSuccess.ShouldBeTrue();
        (await platform.AddDomainAsync(agencyId, secondary, Ct)).IsSuccess.ShouldBeTrue();
        return (primary, secondary);
    }

    private static async Task<IReadOnlyList<string>> SitemapAsync(HttpClient client)
    {
        var xml = await client.GetStringAsync(new Uri("/sitemap.xml", UriKind.Relative), Ct);
        return [.. XDocument.Parse(xml).Descendants(XName.Get("loc", "http://www.sitemaps.org/schemas/sitemap/0.9")).Select(e => e.Value)];
    }
}
