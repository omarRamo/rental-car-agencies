using CarRental.Domain.Referential;

namespace CarRental.Domain.Tests.Referential;

public sealed class LocationTests
{
    private static readonly Guid AgencyId = Guid.CreateVersion7();

    [Fact]
    public void Create_normalizes_the_code_and_raises_an_event()
    {
        var location = Location.Create(AgencyId, " tun ", " Aéroport Tunis-Carthage ", LocationKind.Airport, " ").Value;

        location.Code.ShouldBe("TUN");
        location.Name.ShouldBe("Aéroport Tunis-Carthage");
        location.Kind.ShouldBe(LocationKind.Airport);
        location.Address.ShouldBeNull();
        location.IsActive.ShouldBeTrue();
        location.DomainEvents.ShouldHaveSingleItem().ShouldBe(new LocationAdded(location.Id, AgencyId, "TUN"));
    }

    [Theory]
    [InlineData("T")]
    [InlineData("THIS-CODE-IS-TOO-LONG")]
    [InlineData("TU N")]
    [InlineData("TÜN")]
    [InlineData("")]
    public void Invalid_codes_are_rejected(string code)
    {
        Location.Create(AgencyId, code, "Nom", LocationKind.AgencyOffice).Error.ShouldBe(ReferentialErrors.LocationCodeInvalid);
    }

    [Fact]
    public void Agency_name_and_kind_are_validated()
    {
        Location.Create(Guid.Empty, "TUN", "Nom", LocationKind.Airport).Error.ShouldBe(ReferentialErrors.AgencyRequired);
        Location.Create(AgencyId, "TUN", " ", LocationKind.Airport).Error.ShouldBe(ReferentialErrors.LocationNameInvalid);
        Location.Create(AgencyId, "TUN", "Nom", (LocationKind)99).Error.ShouldBe(ReferentialErrors.LocationKindInvalid);
        Location.Create(AgencyId, "TUN", "Nom", LocationKind.Airport, new string('a', Location.AddressMaxLength + 1)).Error
            .ShouldBe(ReferentialErrors.LocationAddressTooLong);
    }

    [Fact]
    public void Rename_and_activation()
    {
        var location = Location.Create(AgencyId, "AGC", "Agence", LocationKind.AgencyOffice, "Tunis centre").Value;

        location.Rename("Agence centre").IsSuccess.ShouldBeTrue();
        location.Name.ShouldBe("Agence centre");
        location.Rename(" ").Error.ShouldBe(ReferentialErrors.LocationNameInvalid);
        location.Address.ShouldBe("Tunis centre");

        location.Deactivate();
        location.IsActive.ShouldBeFalse();
        location.Activate();
        location.IsActive.ShouldBeTrue();
    }

    [Fact]
    public void The_details_change_but_never_the_code()
    {
        var location = Location.Create(AgencyId, "MIR", "Monastir", LocationKind.Airport, "Aéroport de Monastir").Value;

        location.UpdateDetails(" Hôtel Riadh ", LocationKind.DeliveryPoint, "  ").IsSuccess.ShouldBeTrue();

        location.Code.ShouldBe("MIR");
        location.Name.ShouldBe("Hôtel Riadh");
        location.Kind.ShouldBe(LocationKind.DeliveryPoint);
        location.Address.ShouldBeNull();
        location.AgencyId.ShouldBe(AgencyId);
        location.IsActive.ShouldBeTrue();
    }

    [Fact]
    public void Invalid_details_change_nothing()
    {
        var location = Location.Create(AgencyId, "SOU", "Sousse centre", LocationKind.AgencyOffice, "Boulevard du 14 Janvier").Value;

        location.UpdateDetails(" ", LocationKind.Airport, null).Error.ShouldBe(ReferentialErrors.LocationNameInvalid);
        location.UpdateDetails(new string('n', Location.NameMaxLength + 1), LocationKind.Airport, null).Error.ShouldBe(ReferentialErrors.LocationNameInvalid);
        location.UpdateDetails("Sousse", (LocationKind)42, null).Error.ShouldBe(ReferentialErrors.LocationKindInvalid);
        location.UpdateDetails("Sousse", LocationKind.Airport, new string('a', Location.AddressMaxLength + 1)).Error
            .ShouldBe(ReferentialErrors.LocationAddressTooLong);

        (location.Name, location.Kind, location.Address).ShouldBe(("Sousse centre", LocationKind.AgencyOffice, "Boulevard du 14 Janvier"));
    }
}
