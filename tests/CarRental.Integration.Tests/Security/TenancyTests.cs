using CarRental.Application.Common.Numbering;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Identity.Users;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Referential.Platform;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Identity;
using CarRental.Infrastructure.Persistence;
using CarRental.Infrastructure.Persistence.Interceptors;
using CarRental.Integration.Tests.Support;
using CarRental.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Security;

/// <summary>
/// The agencies never see each other (ADR-0067), on SQL Server: what an operation reads and writes for one agency, what
/// the support does across agencies, the numbers of each agency, the registration of an agency and the agency the
/// support enters.
/// </summary>
public sealed class TenancyTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agency_reads_its_own_rows_only()
    {
        var (first, second) = (await NewAgencyAsync("Lecture A"), await NewAgencyAsync("Lecture B"));

        await using (var scope = host.CreateScope(TestActors.Staff(first.Id, Roles.Manager)))
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            (await db.Set<Agency>().Select(a => a.Id).ToListAsync(Ct)).ShouldBe([first.Id]);
            (await db.Set<Location>().Where(l => l.AgencyId == second.Id).AnyAsync(Ct)).ShouldBeFalse("another agency's locations are invisible");
            (await db.Set<Location>().AnyAsync(l => l.AgencyId == first.Id, Ct)).ShouldBeTrue("the office of the agency, created with it");
        }

        await using (var scope = host.CreateScope(TestActors.Visitor(second.Id)))
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            (await db.Set<Agency>().Select(a => a.Id).ToListAsync(Ct)).ShouldBe([second.Id]);
        }

        await using (var scope = host.CreateScope(UserContextSnapshot.Anonymous()))
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            (await db.Set<Agency>().AnyAsync(Ct)).ShouldBeFalse("an operation without agency sees no agency data");
            (await db.Set<Location>().AnyAsync(Ct)).ShouldBeFalse();
        }

        await using (var scope = host.CreateScope(TestActors.Support()))
        {
            var ids = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Agency>().Select(a => a.Id).ToListAsync(Ct);
            ids.ShouldContain(first.Id);
            ids.ShouldContain(second.Id);
        }
    }

    [Fact]
    public async Task Shared_settings_are_read_by_every_agency_and_agency_values_by_their_agency_only()
    {
        var (first, second) = (await NewAgencyAsync("Valeurs A"), await NewAgencyAsync("Valeurs B"));
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Setting>().Add(Setting.Create(TestSettings.Motto.Key, "Bienvenue à A", first.Id).Value);
            await db.SaveChangesAsync(Ct);
        }

        await using var scopeB = host.CreateScope(TestActors.Staff(second.Id, Roles.Manager));
        var settings = await scopeB.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Setting>()
            .Where(s => s.Key == TestSettings.Motto.Key)
            .ToListAsync(Ct);
        settings.ShouldNotBeEmpty();
        settings.ShouldAllBe(s => s.AgencyId == null);
    }

    [Fact]
    public async Task An_agency_never_writes_the_rows_of_another_agency()
    {
        var (first, second) = (await NewAgencyAsync("Écriture A"), await NewAgencyAsync("Écriture B"));

        await using (var scope = host.CreateScope(TestActors.Staff(first.Id, Roles.Manager)))
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Location>().Add(Location.Create(second.Id, "INTRUS", "Lieu intrus", LocationKind.DeliveryPoint).Value);
            await Should.ThrowAsync<AgencyBoundaryException>(() => db.SaveChangesAsync(Ct));
        }

        await using (var scope = host.CreateScope(TestActors.Staff(first.Id, Roles.Manager)))
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Setting>().Add(Setting.Create(TestSettings.Delay.Key + "_shadow", "01:00:00").Value);
            await Should.ThrowAsync<AgencyBoundaryException>(() => db.SaveChangesAsync(Ct), "a platform value is the support's");
        }

        await using (var scope = host.CreateScope(TestActors.Staff(first.Id, Roles.Manager)))
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Agency>().Add(Agency.Register(new AgencyDetails($"Agence pirate {Guid.NewGuid():N}", "Tunis")).Value);
            await Should.ThrowAsync<AgencyBoundaryException>(() => db.SaveChangesAsync(Ct), "only the support registers an agency");
        }

        await using (var scope = host.CreateScope())
        {
            var location = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Location>().FirstAsync(l => l.AgencyId == first.Id, Ct);
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            db.Entry(location).Property(nameof(Location.AgencyId)).CurrentValue = second.Id;
            await Should.ThrowAsync<AgencyBoundaryException>(() => db.SaveChangesAsync(Ct), "a row never moves to another agency");
        }
    }

    [Fact]
    public async Task Each_agency_has_its_own_series_of_numbers()
    {
        var (first, second) = (await NewAgencyAsync("Numéros A"), await NewAgencyAsync("Numéros B"));
        var year = 7000 + Random.Shared.Next(0, 999);

        (await NextInvoiceAsync(TestActors.SystemFor(first.Id), first.Id, year)).ShouldBe($"FAC-{year}-000001");
        (await NextInvoiceAsync(TestActors.SystemFor(first.Id), first.Id, year)).ShouldBe($"FAC-{year}-000002");
        (await NextInvoiceAsync(TestActors.SystemFor(second.Id), second.Id, year)).ShouldBe($"FAC-{year}-000001");

        await using var scope = host.CreateScope(TestActors.Staff(first.Id, Roles.Accountant));
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var generator = scope.ServiceProvider.GetRequiredService<IDocumentNumberGenerator>();
        await Should.ThrowAsync<InvalidOperationException>(() => unitOfWork.ExecuteAsync(
            async ct =>
            {
                await generator.NextAsync(second.Id, DocumentType.Invoice, year, ct);
                return Result.Success();
            },
            cancellationToken: Ct));
    }

    [Fact]
    public async Task The_support_registers_an_agency_with_its_address_modules_office_and_first_manager()
    {
        var support = await SupportAsync();
        var name = $"Agence Côte {Guid.NewGuid():N}"[..30];
        var managerEmail = $"gerant-{Guid.NewGuid():N}@tests.local";

        await using (var scope = PlatformScope(support))
        {
            var platform = scope.ServiceProvider.GetRequiredService<AgencyPlatformService>();
            var proposed = (await platform.ProposeSlugAsync(name, Ct)).Value;
            proposed.ShouldStartWith("agence-cote-");

            var registered = await platform.RegisterAsync(
                new RegisterAgencyRequest(name, "Hammamet", Slug: null, "Avenue de la plage", "+216 72 000 000", "cote@tests.local", AgencyModuleCatalog.Defaults, managerEmail, "Gérant Côte"),
                Ct);
            registered.IsSuccess.ShouldBeTrue(registered.IsFailure ? registered.Error.ToString() : null);
            registered.Value.Slug.ShouldBe(proposed);

            var twin = await platform.RegisterAsync(
                new RegisterAgencyRequest(name, "Hammamet", Slug: null, null, null, null, AgencyModuleCatalog.Defaults, $"autre-{Guid.NewGuid():N}@tests.local", "Autre"),
                Ct);
            twin.Value.Slug.ShouldBe(proposed + "-2", "a free address is proposed when the name is taken");

            (await platform.RegisterAsync(
                new RegisterAgencyRequest("Agence admin", "Tunis", "admin", null, null, null, AgencyModules.None, $"x-{Guid.NewGuid():N}@tests.local", "X"),
                Ct)).Error.ShouldBe(ReferentialErrors.AgencySlugReserved);
            (await platform.RegisterAsync(
                new RegisterAgencyRequest("Agence doublon", "Tunis", proposed, null, null, null, AgencyModules.None, $"y-{Guid.NewGuid():N}@tests.local", "Y"),
                Ct)).Error.ShouldBe(ReferentialErrors.AgencySlugTaken);
        }

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var agency = await db.Set<Agency>().SingleAsync(a => a.Slug == AgencySlug.FromName(name), Ct);
        agency.Modules.ShouldBe(AgencyModuleCatalog.Defaults);
        agency.HasModule(AgencyModules.WhatsApp).ShouldBeFalse();
        agency.HasModule(AgencyModules.Channels).ShouldBeFalse();
        (await db.Set<Location>().SingleAsync(l => l.AgencyId == agency.Id, Ct)).Kind.ShouldBe(LocationKind.AgencyOffice);
        var manager = await db.Users.SingleAsync(u => u.Email == managerEmail, Ct);
        manager.AgencyId.ShouldBe(agency.Id);
        (await check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().IsInRoleAsync(manager, Roles.Manager)).ShouldBeTrue();
        (await db.Set<AuditLog>().AnyAsync(a => a.Action == ReferentialAuditActions.AgencyRegistered && a.AgencyId == agency.Id, Ct)).ShouldBeTrue();
        (await check.ServiceProvider.GetRequiredService<IAgencyDirectory>().FindBySlugAsync(agency.Slug.ToUpperInvariant(), Ct))!.Id.ShouldBe(agency.Id);
    }

    [Fact]
    public async Task Only_the_support_from_the_platform_pages_manages_the_agencies()
    {
        var agency = await NewAgencyAsync("Gestion");

        await using (var scope = host.CreateScope(TestActors.Staff(agency.Id, Roles.Manager)))
        {
            var platform = scope.ServiceProvider.GetRequiredService<AgencyPlatformService>();
            (await platform.ListAsync(Ct)).Error.ShouldBe(PlatformErrors.Forbidden);
            (await platform.SetModulesAsync(agency.Id, AgencyModuleCatalog.Every, Ct)).Error.ShouldBe(PlatformErrors.Forbidden);
        }

        await using (var scope = host.CreateScope(TestActors.Support(agencyId: agency.Id)))
        {
            (await scope.ServiceProvider.GetRequiredService<AgencyPlatformService>().ListAsync(Ct)).Error.ShouldBe(PlatformErrors.LeaveAgencyFirst);
        }

        await using (var scope = PlatformScope(await SupportAsync()))
        {
            var platform = scope.ServiceProvider.GetRequiredService<AgencyPlatformService>();
            (await platform.SetModulesAsync(agency.Id, AgencyModules.PublicSite | AgencyModules.WhatsApp, Ct)).IsSuccess.ShouldBeTrue();
            (await platform.ChangeSlugAsync(agency.Id, agency.Slug + "-bis", Ct)).IsSuccess.ShouldBeTrue();
            (await platform.SetActiveAsync(agency.Id, active: false, Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var check = host.CreateScope();
        var directory = check.ServiceProvider.GetRequiredService<IAgencyDirectory>();
        var summary = (await directory.FindAsync(agency.Id, Ct))!;
        summary.Slug.ShouldBe(agency.Slug + "-bis");
        summary.Modules.ShouldBe(AgencyModules.PublicSite | AgencyModules.WhatsApp);
        summary.IsActive.ShouldBeFalse();
        summary.HasOpenPublicSite.ShouldBeFalse();
        (await directory.HasModuleAsync(agency.Id, AgencyModules.WhatsApp, Ct)).ShouldBeFalse("a suspended agency has no module");
        (await directory.FindBySlugAsync(agency.Slug, Ct)).ShouldBeNull("the previous address is free of the agency");
        (await check.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<AuditLog>()
            .CountAsync(a => a.AgencyId == agency.Id && a.Action.StartsWith("referential.agency_"), Ct)).ShouldBe(4, "registered, modules, address, suspension");
    }

    [Fact]
    public async Task The_support_enters_an_agency_and_leaves_it_and_both_are_in_its_journal()
    {
        var agency = await NewAgencyAsync("Support");
        var (supportId, supportEmail) = await TestStaff.CreateAsync(host, Roles.Support);
        var support = await TestStaff.ActorAsync(host, supportId, supportEmail, Roles.Support);

        await using (var scope = host.CreateScope(support))
        {
            (await scope.ServiceProvider.GetRequiredService<SupportSessionService>().EnterAgencyAsync(agency.Id, Ct)).IsSuccess.ShouldBeTrue();
        }

        await using (var scope = host.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(supportId.ToString()))!;
            user.SelectedAgencyId.ShouldBe(agency.Id);
            var principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(user);
            principal.FindFirst(CarRentalClaimTypes.Agency)!.Value.ShouldBe(agency.Id.ToString("D"));
            principal.HasClaim(Permissions.ClaimType, Permissions.PlatformAgencies).ShouldBeTrue();
        }

        await using (var scope = host.CreateScope(support with { AgencyId = agency.Id }))
        {
            (await scope.ServiceProvider.GetRequiredService<SupportSessionService>().LeaveAgencyAsync(Ct)).Error
                .ShouldBe(IdentityErrors.PlatformScopeRequired, "entering and leaving run from the platform pages");
        }

        await using (var scope = host.CreateScope(support))
        {
            (await scope.ServiceProvider.GetRequiredService<SupportSessionService>().LeaveAgencyAsync(Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var check = host.CreateScope(TestActors.Staff(agency.Id, Roles.Manager));
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        (await db.Users.SingleAsync(u => u.Id == supportId, Ct)).SelectedAgencyId.ShouldBeNull();
        var journal = await db.Set<AuditLog>().Where(a => a.EntityId == supportId.ToString()).Select(a => a.Action).ToListAsync(Ct);
        journal.ShouldContain(AuditActions.SupportEnteredAgency);
        journal.ShouldContain(AuditActions.SupportLeftAgency);
    }

    [Fact]
    public async Task A_member_of_an_agency_never_receives_a_platform_permission()
    {
        var agency = await NewAgencyAsync("Permissions");
        var (managerId, managerEmail) = await TestStaff.CreateAsync(host, Roles.Manager, agencyId: agency.Id);
        await using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var manager = (await users.FindByIdAsync(managerId.ToString()))!;
        (await users.AddClaimAsync(manager, new System.Security.Claims.Claim(Permissions.ClaimType, Permissions.PlatformAgencies))).Succeeded.ShouldBeTrue();

        var principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(manager);

        principal.HasClaim(Permissions.ClaimType, Permissions.PlatformAgencies).ShouldBeFalse(managerEmail);
        principal.FindFirst(CarRentalClaimTypes.Agency)!.Value.ShouldBe(agency.Id.ToString("D"));
    }

    private async Task<string> NextInvoiceAsync(UserContextSnapshot actor, Guid agencyId, int year)
    {
        await using var scope = host.CreateScope(actor);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var generator = scope.ServiceProvider.GetRequiredService<IDocumentNumberGenerator>();
        string? number = null;
        (await unitOfWork.ExecuteAsync(
            async ct =>
            {
                number = (await generator.NextAsync(agencyId, DocumentType.Invoice, year, ct)).Value;
                return Result.Success();
            },
            cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        return number!;
    }

    /// <summary>A new agency registered by the support (its office location included), returned as the directory sees it.</summary>
    private async Task<AgencySummary> NewAgencyAsync(string label)
    {
        await using var scope = PlatformScope(await SupportAsync());
        var registered = await scope.ServiceProvider.GetRequiredService<AgencyPlatformService>().RegisterAsync(
            new RegisterAgencyRequest($"Agence {label} {Guid.NewGuid():N}", "Sousse", Slug: null, null, null, null, AgencyModuleCatalog.Defaults, $"gerant-{Guid.NewGuid():N}@tests.local", $"Gérant {label}"),
            Ct);
        registered.IsSuccess.ShouldBeTrue(registered.IsFailure ? registered.Error.ToString() : null);
        return (await scope.ServiceProvider.GetRequiredService<IAgencyDirectory>().FindAsync(registered.Value.AgencyId, Ct))!;
    }

    /// <summary>A scope of the platform pages: the support across agencies, the address of the back office for the invitations.</summary>
    private AsyncServiceScope PlatformScope(UserContextSnapshot support)
    {
        var scope = host.CreateScope(support);
        scope.ServiceProvider.GetRequiredService<CarRental.Web.Security.RequestOrigin>().BaseUri = "https://staff.example.test/";
        return scope;
    }

    private async Task<UserContextSnapshot> SupportAsync()
    {
        var (id, email) = await TestStaff.CreateAsync(host, Roles.Support);
        return await TestStaff.ActorAsync(host, id, email, Roles.Support);
    }
}
