using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Administration;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Referential;

namespace CarRental.Application.Tests.Referential;

/// <summary>The pick-up and return locations of the agency of the operation (ADR-0067), on an in-memory context.</summary>
public sealed class LocationAdministrationServiceTests
{
    private static readonly Guid OtherAgencyId = Guid.CreateVersion7();

    private readonly InMemoryAppDbContext _db = new();
    private readonly RecordingAuditTrail _audit = new();
    private readonly InlineUnitOfWork _unitOfWork = new();
    private readonly Location _office = Location.Create(TestUsers.AgencyId, "AGC", "Agence centre", LocationKind.AgencyOffice, "1 avenue Habib Bourguiba").Value;
    private readonly Location _airport = Location.Create(TestUsers.AgencyId, "TUN", "Aéroport Tunis-Carthage", LocationKind.Airport).Value;
    private readonly Location _elsewhere = Location.Create(OtherAgencyId, "SOU", "Agence Sousse", LocationKind.AgencyOffice).Value;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LocationAdministrationServiceTests()
    {
        _db.Rows<Location>().AddRange([_airport, _office, _elsewhere]);
    }

    private LocationAdministrationService ServiceFor(IUserContext context) => new(_db, _unitOfWork, context, _audit);

    private LocationAdministrationService AsManager() => ServiceFor(TestUsers.ContextFor(Guid.CreateVersion7(), "gerant@demo.local", Roles.Manager));

    [Fact]
    public async Task The_locations_of_the_agency_only_are_listed_active_first_by_kind_then_code()
    {
        var delivery = Location.Create(TestUsers.AgencyId, "HTL", "Hôtel Africa", LocationKind.DeliveryPoint).Value;
        var closed = Location.Create(TestUsers.AgencyId, "NBE", "Aéroport Enfidha", LocationKind.Airport).Value;
        closed.Deactivate();
        _db.Rows<Location>().AddRange([delivery, closed]);

        var locations = (await AsManager().ListAsync(Ct)).Value;

        locations.Select(l => l.Code).ShouldBe(["AGC", "TUN", "HTL", "NBE"]);
        locations.ShouldNotContain(l => l.Code == "SOU");
        locations[0].ShouldBe(new LocationAdminItem(_office.Id, "AGC", "Agence centre", LocationKind.AgencyOffice, "1 avenue Habib Bourguiba", IsActive: true));
        locations[^1].IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task A_location_is_added_to_the_agency_with_its_code_and_audited()
    {
        var created = await AsManager().CreateAsync(" mir ", new LocationDetailsForm(" Aéroport Monastir ", LocationKind.Airport, " Monastir "), Ct);

        created.IsSuccess.ShouldBeTrue();
        var location = _db.Rows<Location>().Single(l => l.Id == created.Value);
        (location.AgencyId, location.Code, location.Name, location.Kind, location.Address).ShouldBe(
            (TestUsers.AgencyId, "MIR", "Aéroport Monastir", LocationKind.Airport, "Monastir"));
        var audit = _audit.Events.ShouldHaveSingleItem();
        audit.Action.ShouldBe(ReferentialAuditActions.LocationCreated);
        (audit.EntityType, audit.EntityId, audit.Details, audit.AgencyId).ShouldBe(("Location", location.Id.ToString(), "MIR — Aéroport Monastir", TestUsers.AgencyId));
    }

    [Fact]
    public async Task A_code_is_unique_within_the_agency_only()
    {
        var service = AsManager();

        (await service.CreateAsync("tun", new LocationDetailsForm("Autre aéroport", LocationKind.Airport, null), Ct)).Error.ShouldBe(LocationErrors.CodeTaken);
        (await service.CreateAsync("SOU", new LocationDetailsForm("Sousse", LocationKind.AgencyOffice, null), Ct)).IsSuccess
            .ShouldBeTrue("another agency's code is free in this one");
        (await service.CreateAsync("T", new LocationDetailsForm("Trop court", LocationKind.Airport, null), Ct)).Error.ShouldBe(ReferentialErrors.LocationCodeInvalid);
        (await service.CreateAsync("DJE", new LocationDetailsForm(" ", LocationKind.Airport, null), Ct)).Error.ShouldBe(ReferentialErrors.LocationNameInvalid);

        _db.Rows<Location>().Count(l => l.AgencyId == TestUsers.AgencyId).ShouldBe(3);
        _audit.Events.ShouldHaveSingleItem().Details.ShouldBe("SOU — Sousse");
    }

    [Fact]
    public async Task The_details_of_a_location_change_but_never_its_code()
    {
        var service = AsManager();

        (await service.UpdateAsync(_airport.Id, new LocationDetailsForm("Tunis-Carthage (terminal 1)", LocationKind.Airport, "Aéroport, 2035 Tunis"), Ct)).IsSuccess.ShouldBeTrue();
        (await service.UpdateAsync(_airport.Id, new LocationDetailsForm(" Tunis-Carthage (terminal 1) ", LocationKind.Airport, "Aéroport, 2035 Tunis"), Ct)).IsSuccess
            .ShouldBeTrue("nothing to change");

        (_airport.Code, _airport.Name, _airport.Address).ShouldBe(("TUN", "Tunis-Carthage (terminal 1)", "Aéroport, 2035 Tunis"));
        var audit = _audit.Events.ShouldHaveSingleItem();
        (audit.Action, audit.Details).ShouldBe((ReferentialAuditActions.LocationUpdated, "TUN — Tunis-Carthage (terminal 1)"));
        (await service.UpdateAsync(_airport.Id, new LocationDetailsForm("Tunis", (LocationKind)9, null), Ct)).Error.ShouldBe(ReferentialErrors.LocationKindInvalid);
    }

    [Fact]
    public async Task The_locations_of_another_agency_do_not_exist_for_the_agency()
    {
        var service = AsManager();

        (await service.UpdateAsync(_elsewhere.Id, new LocationDetailsForm("Intrus", LocationKind.Airport, null), Ct)).Error.ShouldBe(LocationErrors.NotFound);
        (await service.SetActiveAsync(_elsewhere.Id, active: false, Ct)).Error.ShouldBe(LocationErrors.NotFound);

        (_elsewhere.Name, _elsewhere.IsActive).ShouldBe(("Agence Sousse", true));
        _audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_location_is_deactivated_and_reactivated_but_the_last_active_one_stays()
    {
        var service = AsManager();

        (await service.SetActiveAsync(_airport.Id, active: false, Ct)).IsSuccess.ShouldBeTrue();
        (await service.SetActiveAsync(_airport.Id, active: false, Ct)).IsSuccess.ShouldBeTrue("already inactive");
        (await service.SetActiveAsync(_office.Id, active: false, Ct)).Error.ShouldBe(LocationErrors.LastActive);
        (await service.SetActiveAsync(_airport.Id, active: true, Ct)).IsSuccess.ShouldBeTrue();

        (_airport.IsActive, _office.IsActive).ShouldBe((true, true));
        _audit.Events.Select(e => (e.Action, e.Details)).ShouldBe(
        [
            (ReferentialAuditActions.LocationActivationChanged, "TUN: inactive"),
            (ReferentialAuditActions.LocationActivationChanged, "TUN: active"),
        ]);
    }

    [Fact]
    public async Task Only_a_settings_manager_working_for_an_agency_manages_its_locations()
    {
        var agent = ServiceFor(TestUsers.ContextFor(Guid.CreateVersion7(), "agent@demo.local", Roles.Agent));
        var support = ServiceFor(TestUsers.ContextFor(Guid.CreateVersion7(), "support@demo.local", Roles.Support));

        (await agent.ListAsync(Ct)).Error.ShouldBe(LocationErrors.Forbidden);
        (await agent.CreateAsync("DJE", new LocationDetailsForm("Djerba", LocationKind.Airport, null), Ct)).Error.ShouldBe(LocationErrors.Forbidden);
        (await agent.SetActiveAsync(_airport.Id, active: false, Ct)).Error.ShouldBe(LocationErrors.Forbidden);
        (await support.ListAsync(Ct)).Error.ShouldBe(ReferentialErrors.AgencyRequired, "the support manages the locations of the agency they entered");
        (await support.UpdateAsync(_airport.Id, new LocationDetailsForm("Tunis", LocationKind.Airport, null), Ct)).Error.ShouldBe(ReferentialErrors.AgencyRequired);

        _airport.IsActive.ShouldBeTrue();
        _audit.Events.ShouldBeEmpty();
    }
}
