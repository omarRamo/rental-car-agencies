using CarRental.Domain.Common;
using CarRental.Web.Components.Public.Search;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace CarRental.Integration.Tests.Public;

/// <summary>Parsing of the deep-link parameters (same names in every language), without host.</summary>
public sealed class SearchQueryTests
{
    private static SearchQuery Parse(string queryString) =>
        SearchQuery.Parse(QueryHelpers.ParseQuery(queryString).Select(p => new KeyValuePair<string, StringValues>(p.Key, p.Value)));

    [Fact]
    public void The_deep_link_of_the_specification_is_read_completely()
    {
        var query = Parse("?lieu=TUN&depart=2027-07-01T10:00&retour=2027-07-05T10:00&cat=ECMR&boite=auto&places=5&devis=Q7F3K2&src=whatsapp");

        query.PickupLocation.ShouldBe("TUN");
        query.ReturnLocation.ShouldBeNull();
        query.Pickup.ShouldBe("2027-07-01T10:00");
        query.Return.ShouldBe("2027-07-05T10:00");
        query.Category!.Value.ShouldBe("ECMR");
        query.Gearbox.ShouldBe(GearboxFilter.Automatic);
        query.MinimumSeats.ShouldBe(5);
        query.QuoteCode.ShouldBe("Q7F3K2");
        query.Traffic.Source.ShouldBe("whatsapp");
        query.Traffic.Channel.ShouldBe(SalesChannel.WhatsApp);
        query.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void Names_are_case_insensitive_and_codes_upper_cased()
    {
        var query = Parse("?LIEU=tun&Retour_Lieu=nbe&CAT=edar&Boite=MANUELLE&SRC=Google");

        query.PickupLocation.ShouldBe("TUN");
        query.ReturnLocation.ShouldBe("NBE");
        query.Category!.Value.ShouldBe("EDAR");
        query.Gearbox.ShouldBe(GearboxFilter.Manual);
        query.Traffic.Source.ShouldBe("google");
        query.Traffic.Channel.ShouldBe(SalesChannel.Web);
    }

    [Theory]
    [InlineData("?cat=ZZZZ&boite=diesel&places=0&tri=random&devis=a!b")]
    [InlineData("?cat=E&places=12&devis=ABC")]
    public void Invalid_optional_values_are_ignored_instead_of_failing_the_search(string queryString)
    {
        var query = Parse(queryString);

        query.Category.ShouldBeNull();
        query.Gearbox.ShouldBe(GearboxFilter.Any);
        query.MinimumSeats.ShouldBeNull();
        query.Sort.ShouldBe(ResultSort.PriceAscending);
        query.QuoteCode.ShouldBeNull();
    }

    [Fact]
    public void The_source_and_the_utm_tags_are_sanitized()
    {
        var query = Parse($"?src=what%27s%20app&utm_source=%20facebook%20&utm_medium=cpc%07&utm_campaign={new string('c', 150)}&utm_term=%20%20");

        query.Traffic.Source.ShouldBeNull();
        query.Traffic.UtmSource.ShouldBe("facebook");
        query.Traffic.UtmMedium.ShouldBe("cpc");
        query.Traffic.UtmCampaign!.Length.ShouldBe(TrafficSource.UtmMaxLength);
        query.Traffic.UtmTerm.ShouldBeNull();
    }

    [Fact]
    public void An_empty_query_is_not_a_search()
    {
        Parse(string.Empty).IsEmpty.ShouldBeTrue();
        Parse("?src=whatsapp&utm_source=bot").IsEmpty.ShouldBeTrue();
        Parse("?info=bientot").Info.ShouldBe(SearchQuery.SoonInfo);
        Parse("?info=other").Info.ShouldBeNull();
    }

    [Fact]
    public void Parameters_are_rebuilt_in_a_stable_order_without_the_notice()
    {
        var query = Parse("?utm_source=meta&src=whatsapp&tri=prix_desc&places=7&boite=auto&cat=IFMR&retour=2027-07-05T10:00&depart=2027-07-01T10:00&retour_lieu=NBE&lieu=TUN&devis=q7f3k2&info=bientot");

        query.ToParameters().Select(p => p.Key).ShouldBe(
            ["lieu", "retour_lieu", "depart", "retour", "cat", "boite", "places", "tri", "devis", "src", "utm_source"]);
        query.ToParameters(includeFilters: false).Select(p => p.Key).ShouldNotContain("boite");
        query.ToQueryString().ShouldBe(
            "?lieu=TUN&retour_lieu=NBE&depart=2027-07-01T10%3A00&retour=2027-07-05T10%3A00&cat=IFMR&boite=auto&places=7&tri=prix_desc&devis=Q7F3K2&src=whatsapp&utm_source=meta");
        Parse(query.ToQueryString()).ShouldBe(query with { Info = null });
    }
}
