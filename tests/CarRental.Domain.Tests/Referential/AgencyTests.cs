using CarRental.Domain.Common;
using CarRental.Domain.Referential;

namespace CarRental.Domain.Tests.Referential;

public sealed class AgencyTests
{
    private static AgencyDetails ValidDetails() => new(
        Name: "  Démo Location  ",
        City: "Tunis",
        LegalName: "Démo Location SARL",
        TaxIdentifier: "1234567/A/M/000",
        Address: "1 avenue Habib Bourguiba",
        Phone: "+216 70 000 000",
        Email: "contact@demo.local");

    [Fact]
    public void Register_creates_an_active_agency_and_raises_an_event()
    {
        var agency = Agency.Register(ValidDetails()).Value;

        agency.Id.ShouldNotBe(Guid.Empty);
        agency.Name.ShouldBe("Démo Location");
        agency.IsActive.ShouldBeTrue();
        agency.IsDeleted.ShouldBeFalse();
        agency.Slug.ShouldBe("demo-location");
        agency.Modules.ShouldBe(AgencyModuleCatalog.Defaults);
        agency.DomainEvents.ShouldHaveSingleItem().ShouldBe(new AgencyRegistered(agency.Id, "Démo Location", "demo-location"));
    }

    [Fact]
    public void Blank_optional_fields_are_stored_as_null()
    {
        var agency = Agency.Register(new AgencyDetails("Agence", "Tunis", LegalName: " ", Email: "")).Value;

        agency.LegalName.ShouldBeNull();
        agency.Email.ShouldBeNull();
    }

    [Theory]
    [InlineData("", "Tunis", "referential.agency_name_invalid")]
    [InlineData("Agence", " ", "referential.agency_city_invalid")]
    public void Name_and_city_are_required(string name, string city, string expectedError)
    {
        Agency.Register(new AgencyDetails(name, city)).Error.Code.ShouldBe(expectedError);
    }

    [Fact]
    public void Too_long_fields_and_invalid_email_are_rejected()
    {
        Agency.Register(new AgencyDetails(new string('a', Agency.NameMaxLength + 1), "Tunis")).Error
            .ShouldBe(ReferentialErrors.AgencyNameInvalid);
        Agency.Register(new AgencyDetails("Agence", "Tunis", Phone: new string('1', Agency.PhoneMaxLength + 1))).Error
            .ShouldBe(ReferentialErrors.AgencyFieldTooLong);
        Agency.Register(new AgencyDetails("Agence", "Tunis", Email: "not-an-email")).Error
            .ShouldBe(ReferentialErrors.AgencyEmailInvalid);
    }

    [Fact]
    public void UpdateDetails_validates_and_raises_an_event()
    {
        var agency = Agency.Register(ValidDetails()).Value;
        agency.DequeueDomainEvents();

        agency.UpdateDetails(new AgencyDetails("Nouveau nom", "Sousse")).IsSuccess.ShouldBeTrue();
        agency.Name.ShouldBe("Nouveau nom");
        agency.City.ShouldBe("Sousse");
        agency.TaxIdentifier.ShouldBeNull();
        agency.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<AgencyDetailsUpdated>();

        agency.UpdateDetails(new AgencyDetails("", "Sousse")).IsFailure.ShouldBeTrue();
        agency.Name.ShouldBe("Nouveau nom");
    }

    [Fact]
    public void Agency_can_be_deactivated_and_reactivated()
    {
        var agency = Agency.Register(ValidDetails()).Value;

        agency.Deactivate();
        agency.IsActive.ShouldBeFalse();
        agency.Activate();
        agency.IsActive.ShouldBeTrue();
    }
}
