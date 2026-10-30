using CarRental.Domain.Common;
using CarRental.Domain.Referential;

namespace CarRental.Domain.Tests.Referential;

/// <summary>The address of an agency's public site (ADR-0067) and the agency's modules.</summary>
public sealed class AgencySlugTests
{
    [Theory]
    [InlineData("Agence Démo Tunis", "agence-demo-tunis")]
    [InlineData("Agence 1", "agence-1")]
    [InlineData("  L'Étoile — Location & Co. ", "l-etoile-location-co")]
    [InlineData("Çà et là", "ca-et-la")]
    [InlineData("AB", "agence-ab")]
    [InlineData("Admin", "agence-admin")]
    [InlineData("وكالة", "agence")]
    [InlineData("", "agence")]
    public void A_slug_is_proposed_from_the_name(string name, string expected)
    {
        AgencySlug.FromName(name).ShouldBe(expected);
        AgencySlug.Normalize(AgencySlug.FromName(name)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_long_name_gives_a_slug_of_at_most_sixty_characters_without_a_trailing_hyphen()
    {
        var slug = AgencySlug.FromName(new string('a', 59) + " bcd");

        slug.Length.ShouldBeLessThanOrEqualTo(AgencySlug.MaxLength);
        slug.ShouldNotEndWith("-");
    }

    [Theory]
    [InlineData(" Agence-1 ", "agence-1")]
    [InlineData("tunis2026", "tunis2026")]
    public void A_valid_slug_is_stored_in_lower_case(string value, string expected)
    {
        AgencySlug.Normalize(value).Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("-agence")]
    [InlineData("agence-")]
    [InlineData("agence--1")]
    [InlineData("agence_1")]
    [InlineData("agence 1")]
    [InlineData("agénce")]
    [InlineData(null)]
    public void An_ill_formed_slug_is_refused(string? value)
    {
        AgencySlug.Normalize(value).Error.ShouldBe(ReferentialErrors.AgencySlugInvalid);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("account")]
    [InlineData("api")]
    [InlineData("support")]
    [InlineData("plateforme")]
    public void A_word_of_the_platform_is_refused(string value)
    {
        AgencySlug.Normalize(value).Error.ShouldBe(ReferentialErrors.AgencySlugReserved);
    }

    [Fact]
    public void A_suffix_keeps_the_slug_within_its_length()
    {
        AgencySlug.WithSuffix("agence-1", 2).ShouldBe("agence-1-2");
        var stem = new string('a', AgencySlug.MaxLength);
        AgencySlug.WithSuffix(stem, 12).ShouldBe(new string('a', AgencySlug.MaxLength - 3) + "-12");
    }

    [Fact]
    public void A_new_agency_has_every_module_but_whatsapp_and_the_channels()
    {
        AgencyModuleCatalog.Defaults.HasFlag(AgencyModules.PublicSite).ShouldBeTrue();
        AgencyModuleCatalog.Defaults.HasFlag(AgencyModules.Cash).ShouldBeTrue();
        AgencyModuleCatalog.Defaults.HasFlag(AgencyModules.WhatsApp).ShouldBeFalse();
        AgencyModuleCatalog.Defaults.HasFlag(AgencyModules.Channels).ShouldBeFalse();
        ((int)AgencyModuleCatalog.Defaults).ShouldBe(249, "121 given by the multi-agency migration, plus 128 (CustomerApi) added by the customer accounts migration");
        AgencyModuleCatalog.All.ShouldBe(Enum.GetValues<AgencyModules>().Where(m => m != AgencyModules.None), ignoreOrder: true);
    }

    [Fact]
    public void The_support_changes_the_address_modules_and_activity_of_an_agency()
    {
        var agency = Agency.Register(new AgencyDetails("Agence Sousse", "Sousse")).Value;
        agency.DequeueDomainEvents();

        agency.ChangeSlug("Sousse-Centre").IsSuccess.ShouldBeTrue();
        agency.SetModules(AgencyModules.PublicSite | AgencyModules.WhatsApp).IsSuccess.ShouldBeTrue();
        agency.Deactivate();
        agency.Deactivate();
        agency.Activate();

        agency.Slug.ShouldBe("sousse-centre");
        agency.HasModule(AgencyModules.WhatsApp).ShouldBeTrue();
        agency.HasModule(AgencyModules.Cash).ShouldBeFalse();
        agency.IsActive.ShouldBeTrue();
        agency.DomainEvents.ShouldBe(
        [
            new AgencySlugChanged(agency.Id, "agence-sousse", "sousse-centre"),
            new AgencyModulesChanged(agency.Id, AgencyModuleCatalog.Defaults, AgencyModules.PublicSite | AgencyModules.WhatsApp),
            new AgencyActivationChanged(agency.Id, IsActive: false),
            new AgencyActivationChanged(agency.Id, IsActive: true),
        ]);
    }

    [Fact]
    public void An_unchanged_address_or_module_set_raises_nothing_and_an_unknown_module_is_refused()
    {
        var agency = Agency.Register(new AgencyDetails("Agence Sfax", "Sfax"), slug: "sfax", modules: AgencyModules.PublicSite).Value;
        agency.DequeueDomainEvents();

        agency.ChangeSlug("SFAX").IsSuccess.ShouldBeTrue();
        agency.SetModules(AgencyModules.PublicSite).IsSuccess.ShouldBeTrue();
        agency.SetModules((AgencyModules)(1 << 20)).Error.ShouldBe(ReferentialErrors.AgencyModulesInvalid);
        agency.ChangeSlug("admin").Error.ShouldBe(ReferentialErrors.AgencySlugReserved);
        Agency.Register(new AgencyDetails("Agence", "Tunis"), slug: "x").Error.ShouldBe(ReferentialErrors.AgencySlugInvalid);

        agency.DomainEvents.ShouldBeEmpty();
        agency.Slug.ShouldBe("sfax");
    }
}
