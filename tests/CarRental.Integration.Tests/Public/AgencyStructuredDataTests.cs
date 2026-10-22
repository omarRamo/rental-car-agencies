using System.Text.Json;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Components.Public.Site;

namespace CarRental.Integration.Tests.Public;

/// <summary>JSON-LD <c>AutoRental</c> of the agency, without host.</summary>
public sealed class AgencyStructuredDataTests
{
    private static readonly Uri SiteUrl = new("https://www.example.tn/");

    internal static PublicAgency Agency(AgencyInfo? info = null) => new(
        new AgencySummary(info?.Id ?? Guid.CreateVersion7(), info?.Name ?? "Agence Démo Tunis", "agence-demo-tunis", "Tunis", true, AgencyModuleCatalog.Defaults),
        info ?? new AgencyInfo(Guid.CreateVersion7(), "Agence Démo Tunis", "Tunis", "+216 70 000 000", "contact@demo.local", true, "1 avenue Habib Bourguiba, 1000 Tunis", 36.7998m, 10.1801m),
        [],
        [],
        new OpeningHours(new TimeOnly(8, 0), new TimeOnly(20, 0)),
        3.40m,
        new PublicPolicies(21, 2, 25, 400, false, 400),
        new MoneyRange(Money.Tnd(90m), Money.Tnd(250m)));

    private static JsonElement Build(PublicAgency agency, string? whatsApp = "21600000000") =>
        JsonDocument.Parse(AgencyStructuredData.Build(agency, "CarRental", SiteUrl, new Uri(SiteUrl, "fr"), whatsApp)).RootElement;

    [Fact]
    public void The_agency_is_described_as_an_auto_rental_business()
    {
        var json = Build(Agency());

        json.GetProperty("@context").GetString().ShouldBe("https://schema.org");
        json.GetProperty("@type").GetString().ShouldBe("AutoRental");
        json.GetProperty("@id").GetString().ShouldBe("https://www.example.tn/#agency");
        json.GetProperty("name").GetString().ShouldBe("CarRental");
        json.GetProperty("url").GetString().ShouldBe("https://www.example.tn/fr");
        json.GetProperty("telephone").GetString().ShouldBe("+216 70 000 000");
        json.GetProperty("email").GetString().ShouldBe("contact@demo.local");
        var address = json.GetProperty("address");
        address.GetProperty("@type").GetString().ShouldBe("PostalAddress");
        address.GetProperty("streetAddress").GetString().ShouldBe("1 avenue Habib Bourguiba, 1000 Tunis");
        address.GetProperty("addressLocality").GetString().ShouldBe("Tunis");
        address.GetProperty("addressCountry").GetString().ShouldBe("TN");
        json.GetProperty("geo").GetProperty("latitude").GetDecimal().ShouldBe(36.7998m);
        json.GetProperty("geo").GetProperty("longitude").GetDecimal().ShouldBe(10.1801m);
        json.GetProperty("hasMap").GetString().ShouldBe("https://www.google.com/maps/search/?api=1&query=36.7998,10.1801");
        json.GetProperty("priceRange").GetString().ShouldBe("90-250 TND");
        json.GetProperty("currenciesAccepted").GetString().ShouldBe("TND");
        json.GetProperty("sameAs").EnumerateArray().Select(e => e.GetString()).ShouldBe(["https://wa.me/21600000000"]);
    }

    [Fact]
    public void Opening_hours_cover_every_day()
    {
        var hours = Build(Agency()).GetProperty("openingHoursSpecification").EnumerateArray().ShouldHaveSingleItem();

        hours.GetProperty("@type").GetString().ShouldBe("OpeningHoursSpecification");
        hours.GetProperty("dayOfWeek").EnumerateArray().Select(d => d.GetString()).ShouldBe(
            ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]);
        hours.GetProperty("opens").GetString().ShouldBe("08:00");
        hours.GetProperty("closes").GetString().ShouldBe("20:00");
    }

    [Fact]
    public void Missing_data_is_left_out_rather_than_invented()
    {
        var bare = Agency(new AgencyInfo(Guid.CreateVersion7(), "Agence", "Sousse", null, null, true)) with { DailyPriceRange = null };

        var json = Build(bare, whatsApp: null);

        json.TryGetProperty("geo", out _).ShouldBeFalse();
        json.TryGetProperty("telephone", out _).ShouldBeFalse();
        json.TryGetProperty("email", out _).ShouldBeFalse();
        json.TryGetProperty("priceRange", out _).ShouldBeFalse();
        json.TryGetProperty("sameAs", out _).ShouldBeFalse();
        json.TryGetProperty("hasMap", out _).ShouldBeFalse();
        json.GetProperty("address").TryGetProperty("streetAddress", out _).ShouldBeFalse();
        json.GetProperty("address").GetProperty("addressLocality").GetString().ShouldBe("Sousse");
    }

    [Fact]
    public void The_json_cannot_close_the_script_element_it_is_inlined_in()
    {
        var hostile = Agency(new AgencyInfo(Guid.CreateVersion7(), "x", "</script><script>alert(1)</script>", null, null, true));

        var raw = AgencyStructuredData.Build(hostile, "Brand & <Co>", SiteUrl, SiteUrl, null);

        raw.ShouldNotContain("</script>");
        raw.ShouldNotContain("<");
        raw.ShouldNotContain("&");
        JsonDocument.Parse(raw).RootElement.GetProperty("name").GetString().ShouldBe("Brand & <Co>");
    }

    [Fact]
    public void The_daily_price_range_comes_from_the_published_grid()
    {
        var categoryRate = new CategoryRateInfo(
            Guid.CreateVersion7(),
            Money.Tnd(110m),
            Money.Tnd(99.5m),
            null,
            Money.Tnd(140m),
            new Dictionary<Guid, Money> { [Guid.CreateVersion7()] = Money.Tnd(260.25m) });
        var plan = new RatePlanSummary(Guid.CreateVersion7(), Guid.CreateVersion7(), null, "Grille", 1, DateTimeOffset.UtcNow, [], [], [categoryRate], []);

        var range = PublicAgencyDirectory.DailyPriceRange(plan)!;

        range.Minimum.ShouldBe(Money.Tnd(99.5m));
        range.Maximum.ShouldBe(Money.Tnd(260.25m));
        PublicAgencyDirectory.DailyPriceRange(null).ShouldBeNull();
        PublicAgencyDirectory.DailyPriceRange(plan with { CategoryRates = [] }).ShouldBeNull();
    }

    [Fact]
    public void The_whatsapp_contact_of_a_site_needs_the_whatsapp_module_of_its_agency()
    {
        var options = new CarRental.Web.Hosting.PublicSiteOptions { WhatsAppNumber = "21600000000" };
        var site = Agency().Site;

        PublicSiteAgency.WhatsAppNumber(site, options).ShouldBeNull("the module is off for a new agency");
        PublicSiteAgency.WhatsAppNumber(site with { Modules = site.Modules | AgencyModules.WhatsApp }, options).ShouldBe("21600000000");
        PublicSiteAgency.WhatsAppNumber(site with { Modules = AgencyModules.WhatsApp }, new CarRental.Web.Hosting.PublicSiteOptions()).ShouldBeNull();
        PublicSiteAgency.WhatsAppNumber(null, options).ShouldBeNull("outside any site");
    }
}
