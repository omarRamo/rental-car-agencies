using CarRental.Web.Components.Public;

namespace CarRental.Integration.Tests.Public;

/// <summary>Localized addresses of the public pages of an agency's site and the language switch, without host (ADR-0067).</summary>
public sealed class PublicRoutesTests
{
    private const string Site = "agence-1";

    [Theory]
    [InlineData(PublicPage.Home, "fr", "/agence-1/fr")]
    [InlineData(PublicPage.Home, "en", "/agence-1/en")]
    [InlineData(PublicPage.Results, "fr", "/agence-1/fr/reserver")]
    [InlineData(PublicPage.Results, "en", "/agence-1/en/book")]
    [InlineData(PublicPage.Agency, "fr", "/agence-1/fr/agence")]
    [InlineData(PublicPage.Agency, "en", "/agence-1/en/agency")]
    [InlineData(PublicPage.Faq, "en", "/agence-1/en/faq")]
    [InlineData(PublicPage.Terms, "fr", "/agence-1/fr/conditions")]
    [InlineData(PublicPage.Terms, "en", "/agence-1/en/terms")]
    [InlineData(PublicPage.Privacy, "fr", "/agence-1/fr/confidentialite")]
    [InlineData(PublicPage.Privacy, "en", "/agence-1/en/privacy")]
    [InlineData(PublicPage.Results, "ar", "/agence-1/ar/reserver")]
    [InlineData(PublicPage.Agency, "EN", "/agence-1/en/agency")]
    public void Each_page_has_one_address_per_culture_on_the_site_of_its_agency(PublicPage page, string culture, string expected)
    {
        PublicRoutes.PathFor(Site, page, culture).ShouldBe(expected);
    }

    [Theory]
    [InlineData("/agence-1/fr/reserver?lieu=TUN&cat=ECMR", "en", "/agence-1/en/book?lieu=TUN&cat=ECMR")]
    [InlineData("/agence-1/en/book", "fr", "/agence-1/fr/reserver")]
    [InlineData("/agence-1/fr/agence", "en", "/agence-1/en/agency")]
    [InlineData("/agence-1/en/privacy", "fr", "/agence-1/fr/confidentialite")]
    [InlineData("/agence-1/fr", "en", "/agence-1/en")]
    [InlineData("/agence-1/fr/reserver/abc123/options?x=1", "en", "/agence-1/en/book/abc123/options?x=1")]
    [InlineData("/agence-1/fr/reserver/abc123/conducteurs", "en", "/agence-1/en/book/abc123/drivers")]
    [InlineData("/agence-1/fr/ma-reservation", "en", "/agence-1/en/my-booking")]
    [InlineData("/agence-1/en/my-booking/file/cancellation", "fr", "/agence-1/fr/ma-reservation/dossier/annulation")]
    [InlineData("/agence-1/fr/unknown-page", "en", "/agence-1/en/unknown-page")]
    [InlineData("/agence-1", "en", "/agence-1/en")]
    [InlineData("/", "en", "/")]
    public void The_language_switch_keeps_the_site_and_translates_the_culture_and_the_slugs(string path, string culture, string expected)
    {
        PublicRoutes.Localize(path, culture).ShouldBe(expected);
    }

    [Theory]
    [InlineData("/agence-1/fr", PublicPage.Home)]
    [InlineData("/agence-1/en/book", PublicPage.Results)]
    [InlineData("/agence-1/en/reserver", PublicPage.Results)]
    [InlineData("/agence-1/fr/agence", PublicPage.Agency)]
    [InlineData("/agence-2/en/terms", PublicPage.Terms)]
    [InlineData("/agence-1/fr/reserver/selection", null)]
    [InlineData("/fr/agence", null)]
    [InlineData("/admin", null)]
    [InlineData("/", null)]
    [InlineData(null, null)]
    public void The_page_of_an_address_is_recognized_in_any_culture(string? path, PublicPage? expected)
    {
        PublicRoutes.PageOf(path).ShouldBe(expected);
    }

    [Fact]
    public void Only_the_slug_of_the_culture_is_canonical()
    {
        PublicRoutes.IsCanonical(PublicPage.Results, "en", "book").ShouldBeTrue();
        PublicRoutes.IsCanonical(PublicPage.Results, "en", "reserver").ShouldBeFalse();
        PublicRoutes.IsCanonical(PublicPage.Agency, "fr", "AGENCE").ShouldBeFalse();
        PublicRoutes.IsCanonical(PublicPage.Home, "fr", null).ShouldBeTrue();
    }

    [Fact]
    public void The_funnel_and_my_booking_of_every_site_are_private_in_every_culture()
    {
        PublicRoutes.PrivatePrefixes("en").ShouldBe(["/*/en/reserver", "/*/en/book", "/*/en/ma-reservation", "/*/en/my-booking"]);
        PublicRoutes.SelectionPath(Site, "fr").ShouldBe("/agence-1/fr/reserver/selection");
        PublicRoutes.SelectionPath(Site, "en").ShouldBe("/agence-1/en/reserver/selection");
        PublicRoutes.SelectionRouteTemplate.ShouldBe("/{agency}/{culture:length(2)}/reserver/selection");
        PublicRoutes.Indexable.ShouldNotContain(PublicPage.Results);
    }
}
