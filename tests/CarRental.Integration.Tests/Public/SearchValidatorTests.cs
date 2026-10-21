using CarRental.Application.Referential.Contracts;
using CarRental.Web.Components.Public.Search;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace CarRental.Integration.Tests.Public;

/// <summary>Validation of a search against the locations of the agency and the clock, without host.</summary>
public sealed class SearchValidatorTests
{
    private static readonly Guid AgencyId = Guid.CreateVersion7();

    /// <summary>20 October 2026, 15:30 in Tunis (UTC+1).</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 14, 30, 0, TimeSpan.Zero);

    private static readonly LocationInfo[] Locations =
    [
        new(Guid.CreateVersion7(), AgencyId, "AGC", "Agence Tunis Centre", LocationKindInfo.AgencyOffice, null),
        new(Guid.CreateVersion7(), AgencyId, "TUN", "Aéroport Tunis-Carthage", LocationKindInfo.Airport, null),
        new(Guid.CreateVersion7(), AgencyId, "NBE", "Aéroport Enfidha-Hammamet", LocationKindInfo.Airport, null),
    ];

    private static SearchValidation Validate(string queryString) =>
        SearchValidator.Validate(
            SearchQuery.Parse(QueryHelpers.ParseQuery(queryString).Select(p => new KeyValuePair<string, StringValues>(p.Key, p.Value))),
            Locations,
            Now);

    [Fact]
    public void A_valid_deep_link_gives_utc_instants_from_tunis_wall_clock_times()
    {
        var validation = Validate("?lieu=tun&depart=2027-07-01T10:00&retour=2027-07-05T10:00:00&cat=ECMR");

        validation.IsValid.ShouldBeTrue();
        var criteria = validation.Criteria!;
        criteria.PickupLocation.Code.ShouldBe("TUN");
        criteria.ReturnLocation.ShouldBeSameAs(criteria.PickupLocation);
        criteria.PickupAt.ShouldBe(new DateTimeOffset(2027, 7, 1, 9, 0, 0, TimeSpan.Zero));
        criteria.ReturnAt.ShouldBe(new DateTimeOffset(2027, 7, 5, 9, 0, 0, TimeSpan.Zero));
        criteria.AgencyId.ShouldBe(AgencyId);
        criteria.Query.PickupLocation.ShouldBe("TUN");
        criteria.Query.Return.ShouldBe("2027-07-05T10:00");
        criteria.Query.Category!.Value.ShouldBe("ECMR");
    }

    [Fact]
    public void Another_return_location_is_kept_and_the_same_one_left_implicit()
    {
        Validate("?lieu=TUN&retour_lieu=NBE&depart=2027-07-01T10:00&retour=2027-07-05T10:00").Criteria!.ReturnLocation.Code.ShouldBe("NBE");
        Validate("?lieu=TUN&retour_lieu=TUN&depart=2027-07-01T10:00&retour=2027-07-05T10:00").Criteria!.Query.ReturnLocation.ShouldBeNull();
    }

    [Theory]
    [InlineData("?depart=2027-07-01T10:00&retour=2027-07-05T10:00", "lieu", "pickup_location_required")]
    [InlineData("?lieu=XYZ&depart=2027-07-01T10:00&retour=2027-07-05T10:00", "lieu", "pickup_location_unknown")]
    [InlineData("?lieu=TUN&retour_lieu=MIR&depart=2027-07-01T10:00&retour=2027-07-05T10:00", "retour_lieu", "return_location_unknown")]
    [InlineData("?lieu=TUN&retour=2027-07-05T10:00", "depart", "pickup_required")]
    [InlineData("?lieu=TUN&depart=01/07/2027&retour=2027-07-05T10:00", "depart", "pickup_invalid")]
    [InlineData("?lieu=TUN&depart=2026-10-20T15:30&retour=2027-07-05T10:00", "depart", "pickup_past")]
    [InlineData("?lieu=TUN&depart=2026-10-19T10:00&retour=2027-07-05T10:00", "depart", "pickup_past")]
    [InlineData("?lieu=TUN&depart=2027-10-21T10:00&retour=2027-10-25T10:00", "depart", "pickup_too_far")]
    [InlineData("?lieu=TUN&depart=2027-07-01T10:00", "retour", "return_required")]
    [InlineData("?lieu=TUN&depart=2027-07-01T10:00&retour=2027-07-05", "retour", "return_invalid")]
    [InlineData("?lieu=TUN&depart=2027-07-05T10:00&retour=2027-07-05T10:00", "retour", "return_before_pickup")]
    [InlineData("?lieu=TUN&depart=2027-07-05T10:00&retour=2027-07-01T10:00", "retour", "return_before_pickup")]
    [InlineData("?lieu=TUN&depart=2027-01-01T10:00&retour=2027-04-02T10:01", "retour", "rental_too_long")]
    public void Invalid_searches_report_the_field_and_the_reason(string queryString, string field, string code)
    {
        var validation = Validate(queryString);

        validation.IsValid.ShouldBeFalse();
        validation.ErrorFor(field).ShouldBe(code);
        SearchValidator.ErrorCodes.ShouldContain(code);
    }

    [Fact]
    public void Every_invalid_field_is_reported_at_once()
    {
        var validation = Validate("?lieu=XYZ&depart=2020-01-01T10:00");

        validation.Errors.Select(e => e.Field).ShouldBe(["lieu", "depart", "retour"]);
    }

    [Fact]
    public void The_limits_are_inclusive()
    {
        Validate("?lieu=TUN&depart=2026-10-20T15:31&retour=2026-10-20T16:00").IsValid.ShouldBeTrue();
        Validate("?lieu=TUN&depart=2027-01-01T10:00&retour=2027-04-01T10:00").IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Instants_round_trip_through_the_deep_link_format()
    {
        var instant = SearchValidator.ParseInstant("2027-07-01T10:00")!.Value;

        SearchValidator.FormatLocal(instant).ShouldBe("2027-07-01T10:00");
        SearchValidator.ParseInstant("2027-02-30T10:00").ShouldBeNull();
        SearchValidator.ParseInstant("tomorrow").ShouldBeNull();
    }
}
