using CarRental.Domain.Referential;

namespace CarRental.Domain.Tests.Public;

/// <summary>Coordinates of the agency office, used by the map link and the structured data of the public site.</summary>
public sealed class AgencyCoordinatesTests
{
    [Fact]
    public void Coordinates_are_kept_with_six_decimals()
    {
        var agency = Agency.Register(new AgencyDetails("Agence", "Tunis", Latitude: 36.79980049m, Longitude: 10.1801m)).Value;

        agency.Latitude.ShouldBe(36.799800m);
        agency.Longitude.ShouldBe(10.180100m);
    }

    [Fact]
    public void An_agency_without_coordinates_stays_valid()
    {
        var agency = Agency.Register(new AgencyDetails("Agence", "Tunis")).Value;

        agency.Latitude.ShouldBeNull();
        agency.Longitude.ShouldBeNull();
    }

    [Theory]
    [InlineData(36.8, null)]
    [InlineData(null, 10.18)]
    [InlineData(90.0001, 10.18)]
    [InlineData(-90.5, 10.18)]
    [InlineData(36.8, 180.1)]
    [InlineData(36.8, -181.0)]
    public void Incomplete_or_out_of_range_coordinates_are_rejected(double? latitude, double? longitude)
    {
        var details = new AgencyDetails("Agence", "Tunis", Latitude: (decimal?)latitude, Longitude: (decimal?)longitude);

        Agency.Register(details).Error.ShouldBe(ReferentialErrors.AgencyCoordinatesInvalid);
    }

    [Fact]
    public void Updating_the_details_replaces_the_coordinates()
    {
        var agency = Agency.Register(new AgencyDetails("Agence", "Tunis", Latitude: 36.8m, Longitude: 10.18m)).Value;

        agency.UpdateDetails(new AgencyDetails("Agence", "Sousse", Latitude: 35.8256m, Longitude: 10.6084m)).IsSuccess.ShouldBeTrue();

        agency.Latitude.ShouldBe(35.8256m);
        agency.Longitude.ShouldBe(10.6084m);
    }
}
