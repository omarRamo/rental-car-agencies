using CarRental.Application.Common.Security;
using CarRental.Application.Common.Tenancy;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Platform;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;

namespace CarRental.Application.Tests.Referential;

/// <summary>The identity of an agency as the support edits it from the platform pages (ADR-0067).</summary>
public sealed class AgencyPlatformServiceTests
{
    private readonly InMemoryAppDbContext _db = new();
    private readonly RecordingAuditTrail _audit = new();
    private readonly Agency _agency = Agency.Register(new AgencyDetails(
        "Agence Côte",
        "Hammamet",
        LegalName: "Agence Côte SARL",
        TaxIdentifier: "1234567/A/M/000",
        Address: "Avenue de la plage",
        Phone: "+216 72 000 000",
        Email: "cote@demo.local",
        Latitude: 36.4m,
        Longitude: 10.6m)).Value;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AgencyPlatformServiceTests()
    {
        _db.Rows<Agency>().Add(_agency);
        _agency.DequeueDomainEvents();
    }

    private AgencyPlatformService ServiceFor(IUserContext context) =>
        new(_db, new InlineUnitOfWork(), context, new AgencyProvisioning([], context), new NoManagerInvitations(), _audit);

    private AgencyPlatformService AsSupport() => ServiceFor(TestUsers.ContextFor(Guid.CreateVersion7(), "support@demo.local", Roles.Support));

    [Fact]
    public async Task The_support_changes_the_name_city_and_contact_details_and_keeps_the_legal_identity()
    {
        var request = new AgencyDetailsRequest(" Agence Côte Nord ", "Nabeul", "12 rue du port", " ", "nord@demo.local");

        (await AsSupport().UpdateDetailsAsync(_agency.Id, request, Ct)).IsSuccess.ShouldBeTrue();

        (_agency.Name, _agency.City, _agency.Address, _agency.Phone, _agency.Email).ShouldBe(("Agence Côte Nord", "Nabeul", "12 rue du port", null, "nord@demo.local"));
        (_agency.LegalName, _agency.TaxIdentifier, _agency.Latitude, _agency.Longitude).ShouldBe(("Agence Côte SARL", "1234567/A/M/000", 36.4m, 10.6m));
        _agency.Slug.ShouldBe("agence-cote", "the address of the public site changes on its own only");
        _agency.DomainEvents.ShouldHaveSingleItem().ShouldBe(new AgencyDetailsUpdated(_agency.Id));
        var audit = _audit.Events.ShouldHaveSingleItem();
        (audit.Action, audit.EntityType, audit.EntityId, audit.Details, audit.AgencyId).ShouldBe(
            (ReferentialAuditActions.AgencyDetailsUpdated, "Agency", _agency.Id.ToString(), "Agence Côte Nord (Nabeul)", _agency.Id));
    }

    [Fact]
    public async Task The_same_details_change_nothing()
    {
        var request = new AgencyDetailsRequest("Agence Côte ", " Hammamet", "Avenue de la plage", "+216 72 000 000", "cote@demo.local");

        (await AsSupport().UpdateDetailsAsync(_agency.Id, request, Ct)).IsSuccess.ShouldBeTrue();

        _agency.DomainEvents.ShouldBeEmpty();
        _audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Invalid_details_are_refused_and_change_nothing()
    {
        var service = AsSupport();

        (await service.UpdateDetailsAsync(_agency.Id, new AgencyDetailsRequest(" ", "Hammamet", null, null, null), Ct)).Error.ShouldBe(ReferentialErrors.AgencyNameInvalid);
        (await service.UpdateDetailsAsync(_agency.Id, new AgencyDetailsRequest("Agence Côte", "Hammamet", null, null, "pas-une-adresse"), Ct)).Error
            .ShouldBe(ReferentialErrors.AgencyEmailInvalid);
        (await service.UpdateDetailsAsync(Guid.CreateVersion7(), new AgencyDetailsRequest("Agence", "Tunis", null, null, null), Ct)).Error.ShouldBe(PlatformErrors.AgencyNotFound);

        (_agency.Name, _agency.Email).ShouldBe(("Agence Côte", "cote@demo.local"));
        _audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_support_outside_any_agency_changes_the_details_of_an_agency()
    {
        var request = new AgencyDetailsRequest("Agence piratée", "Tunis", null, null, null);
        var manager = ServiceFor(TestUsers.ContextFor(Guid.CreateVersion7(), "gerant@demo.local", Roles.Manager));
        var supportInside = new ScopedUserContext([]);
        supportInside.Initialize(UserContextSnapshot.ForUser(Guid.CreateVersion7(), "support@demo.local", RolePresets.PermissionsOf(Roles.Support)) with
        {
            AgencyId = _agency.Id,
            AllAgencies = true,
        });

        (await manager.UpdateDetailsAsync(_agency.Id, request, Ct)).Error.ShouldBe(PlatformErrors.Forbidden);
        (await ServiceFor(supportInside).UpdateDetailsAsync(_agency.Id, request, Ct)).Error.ShouldBe(PlatformErrors.LeaveAgencyFirst);

        _agency.Name.ShouldBe("Agence Côte");
    }

    private sealed class NoManagerInvitations : IAgencyManagerInvitations
    {
        public Task<Result<Guid>> InviteManagerAsync(Guid agencyId, string email, string displayName, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No manager is invited by these tests.");
    }
}
