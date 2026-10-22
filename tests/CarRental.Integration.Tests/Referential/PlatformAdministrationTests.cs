using CarRental.Application.Common.Paging;
using CarRental.Application.Identity.Audit;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Identity.Users;
using CarRental.Application.Referential.Administration;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Referential.Platform;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Support;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Referential;

/// <summary>
/// The screens of the support and of an agency's administration on SQL Server (ADR-0067): an agency registered and managed
/// as the platform pages do, the users and journals of each agency, and the pick-up locations of an agency.
/// </summary>
public sealed class PlatformAdministrationTests(IntegrationHost host)
{
    /// <summary>The code of the office location every registered agency gets (ReferentialAgencyInitializer).</summary>
    private const string OfficeCode = "AGENCE";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_support_registers_an_agency_as_the_dialog_does_then_manages_it_from_its_page()
    {
        var support = await TestAgencies.SupportAsync(host);
        var name = $"Agence Plateforme {Guid.NewGuid():N}";
        var managerEmail = $"gerant-{Guid.NewGuid():N}@tests.local";
        Guid agencyId;

        await using (var scope = TestAgencies.PlatformScope(host, support))
        {
            var platform = scope.ServiceProvider.GetRequiredService<AgencyPlatformService>();
            var proposed = (await platform.ProposeSlugAsync(name, Ct)).Value;
            var modules = AgencyModuleCatalog.Defaults | AgencyModules.WhatsApp;
            var registered = await platform.RegisterAsync(
                new RegisterAgencyRequest(name, "Tabarka", proposed, "Port de plaisance", "+216 78 000 000", "tabarka@tests.local", modules, managerEmail, "Gérant Tabarka"),
                Ct);
            registered.IsSuccess.ShouldBeTrue(registered.IsFailure ? registered.Error.ToString() : null);
            agencyId = registered.Value.AgencyId;
            registered.Value.Slug.ShouldBe(proposed);

            var listed = (await platform.ListAsync(Ct)).Value.Single(a => a.Id == agencyId);
            listed.ShouldBe(new PlatformAgencyItem(agencyId, name, "Tabarka", proposed, true, modules, "+216 78 000 000", "tabarka@tests.local", "Port de plaisance"));

            (await platform.UpdateDetailsAsync(agencyId, new AgencyDetailsRequest(name + " Nord", "Tabarka", "Port", null, "nord@tests.local"), Ct)).IsSuccess.ShouldBeTrue();
            (await platform.InviteManagerAsync(agencyId, $"second-{Guid.NewGuid():N}@tests.local", "Second gérant", Ct)).IsSuccess.ShouldBeTrue();

            var detail = (await platform.GetAsync(agencyId, Ct)).Value;
            (detail.Name, detail.Address, detail.Phone, detail.Email).ShouldBe((name + " Nord", "Port", null, "nord@tests.local"));

            var users = (await scope.ServiceProvider.GetRequiredService<UserAdministrationService>().ListAgencyUsersAsync(agencyId, new PageRequest(), Ct)).Value;
            users.Items.Count.ShouldBe(2);
            users.Items.ShouldAllBe(u => u.Roles.Contains(Roles.Manager) && u.AgencyId == agencyId);
            users.Items.ShouldContain(u => u.Email == managerEmail);
        }

        await using var check = host.CreateScope();
        (await check.ServiceProvider.GetRequiredService<IAgencyDirectory>().FindAsync(agencyId, Ct))!.Name.ShouldBe(name + " Nord", "the directory drops its copy");
        var agency = await check.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Agency>().SingleAsync(a => a.Id == agencyId, Ct);
        agency.City.ShouldBe("Tabarka");
        (await check.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<AuditLog>()
            .AnyAsync(a => a.AgencyId == agencyId && a.Action == ReferentialAuditActions.AgencyDetailsUpdated && a.UserId == support.UserId, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_manager_reads_the_journal_of_the_agency_and_the_support_every_journal_by_agency()
    {
        var (first, second) = (await TestAgencies.RegisterAsync(host, "Journal A", Ct), await TestAgencies.RegisterAsync(host, "Journal B", Ct));
        var registered = new AuditLogFilter { Action = ReferentialAuditActions.AgencyRegistered };

        await using (var scope = host.CreateScope(TestActors.Staff(first.Id, Roles.Manager)))
        {
            var entries = (await scope.ServiceProvider.GetRequiredService<AuditLogQueries>().SearchAsync(registered, new PageRequest(1, 200), Ct)).Value.Items;
            entries.ShouldHaveSingleItem().AgencyId.ShouldBe(first.Id);
        }

        await using (var scope = host.CreateScope(TestActors.Support()))
        {
            var queries = scope.ServiceProvider.GetRequiredService<AuditLogQueries>();
            var ofSecond = (await queries.SearchAsync(registered with { AgencyId = second.Id }, new PageRequest(1, 200), Ct)).Value.Items;
            ofSecond.ShouldHaveSingleItem().Details!.ShouldContain(second.Name);

            var everywhere = (await queries.SearchAsync(registered, new PageRequest(1, 200), Ct)).Value.Items;
            everywhere.Select(e => e.AgencyId).ShouldContain(first.Id);
            everywhere.Select(e => e.AgencyId).ShouldContain(second.Id);

            var platform = (await queries.SearchAsync(new AuditLogFilter { PlatformOnly = true }, new PageRequest(1, 50), Ct)).Value.Items;
            platform.ShouldAllBe(e => e.AgencyId == null);
        }
    }

    [Fact]
    public async Task The_manager_of_an_agency_manages_its_locations_and_never_those_of_another()
    {
        var (first, second) = (await TestAgencies.RegisterAsync(host, "Lieux A", Ct), await TestAgencies.RegisterAsync(host, "Lieux B", Ct));
        var managerA = TestActors.Staff(first.Id, Roles.Manager);
        var managerB = TestActors.Staff(second.Id, Roles.Manager);
        Guid airportA;

        await using (var scope = host.CreateScope(managerA))
        {
            var locations = scope.ServiceProvider.GetRequiredService<LocationAdministrationService>();
            airportA = (await locations.CreateAsync("TBJ", new LocationDetailsForm("Aéroport Tabarka", LocationKind.Airport, null), Ct)).Value;
            (await locations.CreateAsync("tbj", new LocationDetailsForm("Doublon", LocationKind.Airport, null), Ct)).Error.ShouldBe(LocationErrors.CodeTaken);
            (await locations.UpdateAsync(airportA, new LocationDetailsForm("Aéroport Tabarka-Aïn Draham", LocationKind.Airport, "7150 Tabarka"), Ct)).IsSuccess.ShouldBeTrue();
        }

        await using (var scope = host.CreateScope(managerB))
        {
            var locations = scope.ServiceProvider.GetRequiredService<LocationAdministrationService>();
            (await locations.CreateAsync("TBJ", new LocationDetailsForm("Point Tabarka", LocationKind.DeliveryPoint, null), Ct)).IsSuccess
                .ShouldBeTrue("a code is unique within an agency only");
            (await locations.UpdateAsync(airportA, new LocationDetailsForm("Intrus", LocationKind.Airport, null), Ct)).Error.ShouldBe(LocationErrors.NotFound);
            (await locations.SetActiveAsync(airportA, active: false, Ct)).Error.ShouldBe(LocationErrors.NotFound);
            (await locations.ListAsync(Ct)).Value.Select(l => l.Code).ShouldBe([OfficeCode, "TBJ"], ignoreOrder: true);
        }

        await using (var scope = host.CreateScope(managerA))
        {
            var locations = scope.ServiceProvider.GetRequiredService<LocationAdministrationService>();
            var office = (await locations.ListAsync(Ct)).Value.Single(l => l.Code == OfficeCode);
            (await locations.SetActiveAsync(airportA, active: false, Ct)).IsSuccess.ShouldBeTrue();
            (await locations.SetActiveAsync(office.Id, active: false, Ct)).Error.ShouldBe(LocationErrors.LastActive);
            (await scope.ServiceProvider.GetRequiredService<IReferentialQueries>().GetLocationsAsync(first.Id, Ct)).Select(l => l.Code)
                .ShouldBe([OfficeCode], "a deactivated location is no longer offered");
        }

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var airport = await db.Set<Location>().SingleAsync(l => l.Id == airportA, Ct);
        (airport.AgencyId, airport.Code, airport.Name, airport.Address, airport.IsActive).ShouldBe((first.Id, "TBJ", "Aéroport Tabarka-Aïn Draham", "7150 Tabarka", false));
        var journal = await db.Set<AuditLog>().Where(a => a.EntityId == airportA.ToString()).Select(a => new { a.Action, a.AgencyId }).ToListAsync(Ct);
        journal.ShouldAllBe(a => a.AgencyId == first.Id);
        journal.Select(a => a.Action).ShouldContain(ReferentialAuditActions.LocationCreated);
        journal.Select(a => a.Action).ShouldContain(ReferentialAuditActions.LocationUpdated);
        journal.Select(a => a.Action).ShouldContain(ReferentialAuditActions.LocationActivationChanged);
        journal.Select(a => a.Action).ShouldContain(AuditActions.EntityUpdated);
    }
}
