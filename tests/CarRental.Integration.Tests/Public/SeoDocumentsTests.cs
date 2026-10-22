using System.Xml.Linq;
using CarRental.Web.Endpoints.PublicSite;

namespace CarRental.Integration.Tests.Public;

/// <summary>Content of sitemap.xml and robots.txt, without host: every open site of an agency, at its address (ADR-0067).</summary>
public sealed class SeoDocumentsTests
{
    private static readonly Uri BaseUri = new("https://www.example.tn/");
    private static readonly XNamespace Sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    [Fact]
    public void The_sitemap_lists_every_indexable_page_of_every_site_in_every_culture_with_absolute_addresses()
    {
        var document = XDocument.Parse(SeoEndpoints.Sitemap(BaseUri, ["agence-1", "agence-2"], ["fr", "en"]));

        var locations = document.Root!.Elements(Sitemap + "url").Select(u => u.Element(Sitemap + "loc")!.Value).ToList();
        locations.ShouldBe(
        [
            "https://www.example.tn/agence-1/fr", "https://www.example.tn/agence-1/en",
            "https://www.example.tn/agence-1/fr/agence", "https://www.example.tn/agence-1/en/agency",
            "https://www.example.tn/agence-1/fr/faq", "https://www.example.tn/agence-1/en/faq",
            "https://www.example.tn/agence-1/fr/conditions", "https://www.example.tn/agence-1/en/terms",
            "https://www.example.tn/agence-1/fr/confidentialite", "https://www.example.tn/agence-1/en/privacy",
            "https://www.example.tn/agence-2/fr", "https://www.example.tn/agence-2/en",
            "https://www.example.tn/agence-2/fr/agence", "https://www.example.tn/agence-2/en/agency",
            "https://www.example.tn/agence-2/fr/faq", "https://www.example.tn/agence-2/en/faq",
            "https://www.example.tn/agence-2/fr/conditions", "https://www.example.tn/agence-2/en/terms",
            "https://www.example.tn/agence-2/fr/confidentialite", "https://www.example.tn/agence-2/en/privacy",
        ]);
        locations.ShouldNotContain(l => l.Contains("reserver", StringComparison.Ordinal) || l.Contains("book", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_url_carries_its_hreflang_alternates_and_x_default()
    {
        var document = XDocument.Parse(SeoEndpoints.Sitemap(BaseUri, ["agence-1"], ["fr", "en"]));
        var agency = document.Root!.Elements(Sitemap + "url").Single(u => u.Element(Sitemap + "loc")!.Value == "https://www.example.tn/agence-1/en/agency");
        var home = document.Root.Elements(Sitemap + "url").First();

        agency.Elements(Xhtml + "link").Select(l => (l.Attribute("hreflang")!.Value, l.Attribute("href")!.Value)).ShouldBe(
        [
            ("fr", "https://www.example.tn/agence-1/fr/agence"),
            ("en", "https://www.example.tn/agence-1/en/agency"),
            ("x-default", "https://www.example.tn/agence-1/fr/agence"),
        ]);
        home.Elements(Xhtml + "link").Last().Attribute("href")!.Value.ShouldBe("https://www.example.tn/agence-1", "the address of the site redirects to the visitor's language");
        agency.Elements(Xhtml + "link").ShouldAllBe(l => l.Attribute("rel")!.Value == "alternate");
    }

    [Fact]
    public void Robots_keeps_the_private_areas_and_the_funnel_out_and_references_the_sitemap()
    {
        var robots = SeoEndpoints.Robots(BaseUri, ["fr", "en", "ar"], allowIndexing: true);
        var lines = robots.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines[0].ShouldBe("User-agent: *");
        foreach (var path in new[] { "/admin", "/account", "/api/", "/*/api/", "/*/fr/reserver", "/*/en/book", "/*/en/reserver", "/*/fr/ma-reservation", "/*/en/my-booking", "/*/ar/reserver" })
        {
            lines.ShouldContain($"Disallow: {path}");
        }

        lines.ShouldContain("Allow: /");
        lines.ShouldNotContain("Disallow: /");
        lines.Last().ShouldBe("Sitemap: https://www.example.tn/sitemap.xml");
        robots.ShouldNotContain("\r");
    }

    [Fact]
    public void A_platform_without_open_site_has_an_empty_sitemap()
    {
        var document = XDocument.Parse(SeoEndpoints.Sitemap(BaseUri, [], ["fr", "en"]));

        document.Root!.Elements(Sitemap + "url").ShouldBeEmpty();
    }

    [Fact]
    public void Robots_hides_the_whole_site_when_indexing_is_off()
    {
        SeoEndpoints.Robots(BaseUri, ["fr", "en"], allowIndexing: false).ShouldBe("User-agent: *\nDisallow: /\n");
    }
}
