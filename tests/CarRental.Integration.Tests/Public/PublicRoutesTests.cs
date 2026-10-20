using CarRental.Web.Components.Public;

namespace CarRental.Integration.Tests.Public;

/// <summary>Localized addresses of the public pages and the language switch, without host.</summary>
public sealed class PublicRoutesTests
{
    [Theory]
    [InlineData(PublicPage.Home, "fr", "/fr")]
    [InlineData(PublicPage.Home, "en", "/en")]
    [InlineData(PublicPage.Results, "fr", "/fr/reserver")]
    [InlineData(PublicPage.Results, "en", "/en/book")]
    [InlineData(PublicPage.Agency, "fr", "/fr/agence")]
    [InlineData(PublicPage.Agency, "en", "/en/agency")]
    [InlineData(PublicPage.Faq, "en", "/en/faq")]
    [InlineData(PublicPage.Terms, "fr", "/fr/conditions")]
    [InlineData(PublicPage.Terms, "en", "/en/terms")]
    [InlineData(PublicPage.Privacy, "fr", "/fr/confidentialite")]
    [InlineData(PublicPage.Privacy, "en", "/en/privacy")]
    [InlineData(PublicPage.Results, "ar", "/ar/reserver")]
    [InlineData(PublicPage.Agency, "EN", "/en/agency")]
    public void Each_page_has_one_address_per_culture(PublicPage page, string culture, string expected)
    {
        PublicRoutes.PathFor(page, culture).ShouldBe(expected);
    }

    [Theory]
    [InlineData("/fr/reserver?lieu=TUN&cat=ECMR", "en", "/en/book?lieu=TUN&cat=ECMR")]
    [InlineData("/en/book", "fr", "/fr/reserver")]
    [InlineData("/fr/agence", "en", "/en/agency")]
    [InlineData("/en/privacy", "fr", "/fr/confidentialite")]
    [InlineData("/fr", "en", "/en")]
    [InlineData("/fr/reserver/abc123/options?x=1", "en", "/en/book/abc123/options?x=1")]
    [InlineData("/fr/ma-reservation", "en", "/en/my-booking")]
    [InlineData("/fr/unknown-page", "en", "/en/unknown-page")]
    [InlineData("/account/login", "en", "/en")]
    public void The_language_switch_translates_the_culture_and_the_first_slug(string path, string culture, string expected)
    {
        PublicRoutes.Localize(path, culture).ShouldBe(expected);
    }

    [Theory]
    [InlineData("/fr", PublicPage.Home)]
    [InlineData("/en/book", PublicPage.Results)]
    [InlineData("/en/reserver", PublicPage.Results)]
    [InlineData("/fr/agence", PublicPage.Agency)]
    [InlineData("/en/terms", PublicPage.Terms)]
    [InlineData("/fr/reserver/selection", null)]
    [InlineData("/admin", null)]
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
    public void The_funnel_and_my_booking_are_private_in_every_culture()
    {
        PublicRoutes.PrivatePrefixes("en").ShouldBe(["/en/reserver", "/en/book", "/en/ma-reservation", "/en/my-booking"]);
        PublicRoutes.SelectionPath("fr").ShouldBe("/fr/reserver/selection");
        PublicRoutes.SelectionPath("en").ShouldBe("/en/reserver/selection");
        PublicRoutes.SelectionRouteTemplate.ShouldBe("/{culture:length(2)}/reserver/selection");
        PublicRoutes.Indexable.ShouldNotContain(PublicPage.Results);
    }
}
