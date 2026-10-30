using System.Text.RegularExpressions;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Referential;
using CarRental.E2E.Tests.Support;
using CarRental.Infrastructure.Persistence;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests;

/// <summary>
/// The screens of the support (ADR-0067) and the agency's own administration on a 10-inch tablet: registering an agency
/// and entering it, the page of an agency, the separation seen by the manager of another agency, the pick-up locations.
/// Support accounts are created per test: the agency a support account entered belongs to the account.
/// </summary>
public sealed partial class PlatformScreensTests(E2EHost host) : BrowserTest(host)
{
    protected override ViewportSize Viewport => new() { Width = 1280, Height = 800 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_support_registers_an_agency_and_enters_its_back_office()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = $"Agence E2E Plateforme {suffix}";
        await BackOfficeSession.SignInAsync(Page, await BackOfficeSession.NewStaffAsync(Host, Roles.Support), "/admin/platform");
        await WaitUntilReadyAsync();
        await Expect(Page.GetByTestId("admin-agency")).ToHaveTextAsync("Plateforme");
        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Comptes support" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Flotte et échéances" })).ToHaveCountAsync(0);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Nouvelle agence" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await dialog.GetByLabel("Nom de l'agence").FillAsync(name);
        await Expect(dialog.GetByLabel("Adresse de la vitrine")).ToHaveValueAsync($"agence-e2e-plateforme-{suffix}");
        await dialog.GetByLabel("Ville").FillAsync("Tabarka");
        await dialog.GetByLabel("E-mail du gérant").FillAsync($"gerant-{suffix}@tests.local");
        await dialog.GetByLabel("Nom du gérant").FillAsync("Gérant Tabarka");
        await Expect(dialog.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = "Vitrine" })).ToBeCheckedAsync();
        await Expect(dialog.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = "WhatsApp" })).Not.ToBeCheckedAsync();
        await Expect(dialog.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = "Booking.com et canaux" })).Not.ToBeCheckedAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await dialog.GetByTestId("register-submit").ClickAsync();

        await Expect(Page.GetByText($"{name} est créée")).ToBeVisibleAsync();
        var row = Page.Locator("tr", new PageLocatorOptions { Has = Page.Locator($"[data-agency-slug='agence-e2e-plateforme-{suffix}']") });
        await Expect(row).ToContainTextAsync($"/agence-e2e-plateforme-{suffix}/fr");
        await Expect(row).ToContainTextAsync("Active");
        await BackOfficeSession.CloseSnackbarsAsync(Page);

        // The way into the agency: its back office opens as for its manager, with the banner of the support.
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Ouvrir le back office de {name}" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(AdminHome());
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);
        await Expect(Page.GetByTestId("admin-agency")).ToHaveTextAsync(name);
        await Expect(Page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = $"Vous intervenez dans l'agence {name}" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Lieux de prise en charge" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Agences", Exact = true })).ToBeVisibleAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Quitter l'agence" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(PlatformHome());
        await WaitUntilReadyAsync();
        await Expect(Page.GetByTestId("admin-agency")).ToHaveTextAsync("Plateforme");
    }

    [Fact]
    public async Task The_support_manages_an_agency_from_its_page()
    {
        var agencyId = await BackOfficeSession.NewAgencyAsync(Host, "Fiche");
        var slug = $"e2e-fiche-{Guid.NewGuid().ToString("N")[..8]}";
        var managerEmail = $"gerant-{Guid.NewGuid():N}@tests.local";
        await BackOfficeSession.SignInAsync(Page, await BackOfficeSession.NewStaffAsync(Host, Roles.Support), $"/admin/platform/agencies/{agencyId:D}");
        await WaitUntilReadyAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        // Modules: WhatsApp switched on for the agency.
        await Page.GetByLabel("WhatsApp", new PageGetByLabelOptions { Exact = true }).CheckAsync();
        await Page.GetByTestId("agency-save-modules").ClickAsync();
        await Expect(Page.GetByText("Modules enregistrés.")).ToBeVisibleAsync();

        // A new address for the public site, after the warning and a confirmation.
        await Page.GetByLabel("Nouvelle adresse de la vitrine").FillAsync(slug);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Changer l'adresse" }).ClickAsync();
        var confirm = Page.GetByRole(AriaRole.Dialog);
        await Expect(confirm).ToContainTextAsync($"/{slug}/fr");
        await confirm.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Changer l'adresse" }).ClickAsync();
        await Expect(Page.GetByTestId("agency-site")).ToHaveTextAsync($"/{slug}/fr");

        // A domain of the agency's own site (ADR-0069): added (the first one is primary), then removed after a confirmation.
        var domain = $"www.{Guid.NewGuid().ToString("N")[..8]}.example.test";
        await Expect(Page.GetByTestId("agency-domains-empty")).ToBeVisibleAsync();
        await Page.GetByLabel("Nom de domaine à ajouter").FillAsync(domain);
        await Page.GetByTestId("agency-domain-add").ClickAsync();
        await Expect(Page.GetByText("Domaine ajouté.")).ToBeVisibleAsync();
        var row = Page.Locator($"[data-agency-domain='{domain}']");
        await Expect(row).ToBeVisibleAsync();
        await Expect(row.GetByTestId("agency-domain-primary")).ToBeVisibleAsync();
        await BackOfficeSession.CloseSnackbarsAsync(Page);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Retirer {domain}" }).ClickAsync();
        await confirm.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retirer" }).ClickAsync();
        await Expect(Page.GetByText("Domaine retiré.")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("agency-domains-empty")).ToBeVisibleAsync();

        // Suspended, then active again.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Suspendre l'agence" }).ClickAsync();
        await confirm.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Suspendre l'agence" }).ClickAsync();
        await Expect(Page.GetByText("Agence suspendue.")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-agency-status='suspended']")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Réactiver l'agence" }).ClickAsync();
        await confirm.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Réactiver l'agence" }).ClickAsync();
        await Expect(Page.Locator("[data-agency-status='active']")).ToBeVisibleAsync();

        // A manager invited from the page joins the users of the agency.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Inviter un gérant" }).ClickAsync();
        var invite = Page.GetByRole(AriaRole.Dialog);
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await invite.GetByLabel("Adresse e-mail").FillAsync(managerEmail);
        await invite.GetByLabel("Nom affiché").FillAsync("Gérant Fiche");
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await invite.GetByTestId("invite-submit").ClickAsync();
        await Expect(Page.Locator($"[data-user-email='{managerEmail}']")).ToBeVisibleAsync();
        await BackOfficeSession.CloseSnackbarsAsync(Page);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await using var scope = Scope();
        var agency = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Agency>().AsNoTracking().SingleAsync(a => a.Id == agencyId, Ct);
        (agency.Slug, agency.IsActive, agency.HasModule(Domain.Common.AgencyModules.WhatsApp)).ShouldBe((slug, true, true));
    }

    [Fact]
    public async Task The_support_invites_a_support_account_then_locks_and_unlocks_it()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = $"assistance-{suffix}@tests.local";

        // Named to sort before the other support accounts of the run: it is on the first page.
        var name = $"Assistance E2E {suffix}";
        await BackOfficeSession.SignInAsync(Page, await BackOfficeSession.NewStaffAsync(Host, Roles.Support), "/admin/platform");
        await WaitUntilReadyAsync();
        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Comptes support" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(SupportAccounts());
        await Expect(Page.Locator("table tbody tr").First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Inviter un compte support" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await dialog.GetByLabel("Adresse e-mail").FillAsync(email);
        await dialog.GetByLabel("Nom affiché").FillAsync(name);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await dialog.GetByTestId("invite-submit").ClickAsync();

        var row = Page.Locator("tr", new PageLocatorOptions { Has = Page.Locator($"[data-user-email='{email}']") });
        await Expect(row).ToContainTextAsync("Support de la plateforme");
        await Expect(row).ToContainTextAsync("Actif");
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Verrouiller {name}" }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Verrouiller", Exact = true }).ClickAsync();
        await Expect(row).ToContainTextAsync("Verrouillé");
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Déverrouiller {name}" }).ClickAsync();
        await Expect(row).ToContainTextAsync("Actif");
        await BackOfficeSession.CloseSnackbarsAsync(Page);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
    }

    [Fact]
    public async Task The_manager_of_sousse_sees_the_agency_of_sousse_only()
    {
        await BackOfficeSession.SignInAsync(Page, TestAccounts.SecondAgencyManagerEmail, "/admin");
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);
        await Expect(Page.GetByTestId("admin-agency")).ToHaveTextAsync("Agence Démo Sousse");
        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Agences", Exact = true })).ToHaveCountAsync(0);

        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Lieux de prise en charge" }).ClickAsync();
        await WaitUntilReadyAsync();
        await Expect(Page.Locator("[data-location-code='SOU']")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-location-code='MIR']")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-location-code='TUN']")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-location-code='AGC']")).ToHaveCountAsync(0);

        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Identité de l'agence" }).ClickAsync();
        await WaitUntilReadyAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Vendeur : Agence Démo Sousse" })).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("agency-site")).ToContainTextAsync("/agence-demo-sousse");
        await Expect(Page.GetByText("contactez le support")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Agence" })).ToHaveCountAsync(0);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Utilisateurs et rôles" }).ClickAsync();
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);
        await Expect(Page.Locator($"[data-user-email='{TestAccounts.SecondAgencyManagerEmail}']")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Expect(Page.Locator($"[data-user-email='{TestAccounts.ManagerEmail}']")).ToHaveCountAsync(0);
        await Expect(Page.Locator($"[data-user-email='{TestAccounts.SupportEmail}']")).ToHaveCountAsync(0);

        await Page.GotoAsync("/admin/platform");
        await Expect(Page).ToHaveURLAsync(AccessDenied());
    }

    [Fact]
    public async Task A_manager_fills_in_the_seller_identity_of_the_agency_and_reads_its_public_site_and_modules()
    {
        var agencyId = await BackOfficeSession.NewAgencyAsync(Host, "Identité");
        await BackOfficeSession.SignInAsync(Page, await BackOfficeSession.NewStaffAsync(Host, Roles.Manager, agencyId), "/admin/agency");
        await WaitUntilReadyAsync();
        await Expect(Page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = "Identité incomplète" })).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("agency-site")).ToContainTextAsync("/agence-e2e-identite-");
        await Expect(Page.Locator("[data-agency-modules] [data-module='PublicSite']")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-agency-modules] [data-module='WhatsApp']")).ToHaveCountAsync(0);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        var digits = Random.Shared.Next(1_000_000, 9_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Page.GetByLabel("Raison sociale").FillAsync($"Location E2E {digits} SARL");
        await Page.GetByLabel("Matricule fiscal").FillAsync($"MF-E2E-{digits}");
        await Page.GetByLabel("Adresse du siège").FillAsync("12 rue des Essais, 7000 Bizerte");
        await Page.GetByTestId("agency-save").ClickAsync();

        await Expect(Page.GetByText("Identité de l'agence enregistrée.")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = "Identité incomplète" })).ToHaveCountAsync(0);
        await BackOfficeSession.CloseSnackbarsAsync(Page);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await using var scope = Scope();
        var agency = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Agency>().AsNoTracking().SingleAsync(a => a.Id == agencyId, Ct);
        (agency.LegalName, agency.TaxIdentifier, agency.LegalAddress).ShouldBe(($"Location E2E {digits} SARL", $"MF-E2E-{digits}", "12 rue des Essais, 7000 Bizerte"));
    }

    [Fact]
    public async Task A_manager_adds_edits_and_deactivates_a_pick_up_location()
    {
        var agencyId = await BackOfficeSession.NewAgencyAsync(Host, "Lieux");
        var code = $"E2E-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        await BackOfficeSession.SignInAsync(Page, await BackOfficeSession.NewStaffAsync(Host, Roles.Manager, agencyId), "/admin/locations");
        await WaitUntilReadyAsync();
        await Expect(Page.Locator("[data-location-code='AGENCE']")).ToBeVisibleAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Nouveau lieu" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await dialog.GetByLabel("Code", new LocatorGetByLabelOptions { Exact = true }).FillAsync(code.ToLowerInvariant());
        await dialog.GetByLabel("Nom", new LocatorGetByLabelOptions { Exact = true }).FillAsync("Aéroport E2E");
        var kind = dialog.GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = "Type de lieu" });
        await kind.ClickAsync();
        await BackOfficeSession.WaitForOpenPopoverAsync(Page);
        await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = "Aéroport" }).ClickAsync();

        // The render of the chosen kind redraws the dialog: an address typed before it arrives is wiped.
        await Expect(kind).ToHaveValueAsync("Aéroport");
        var address = dialog.GetByLabel("Adresse", new LocatorGetByLabelOptions { Exact = true });
        await address.FillAsync("Route de l'aéroport");
        await Expect(address).ToHaveValueAsync("Route de l'aéroport");
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await dialog.GetByTestId("location-submit").ClickAsync();

        var row = Page.Locator("tr", new PageLocatorOptions { Has = Page.Locator($"[data-location-code='{code}']") });
        await Expect(row).ToContainTextAsync("Aéroport E2E");
        await Expect(row).ToContainTextAsync("Aéroport");
        await Expect(row).ToContainTextAsync("Route de l'aéroport");
        await Expect(row).ToContainTextAsync("Actif");

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Modifier le lieu {code}" }).ClickAsync();
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await Expect(dialog.GetByLabel("Code", new LocatorGetByLabelOptions { Exact = true })).ToHaveAttributeAsync("readonly", string.Empty);
        await dialog.GetByLabel("Nom", new LocatorGetByLabelOptions { Exact = true }).FillAsync("Aéroport E2E (arrivées)");
        await dialog.GetByTestId("location-submit").ClickAsync();
        await Expect(row).ToContainTextAsync("Aéroport E2E (arrivées)");

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Désactiver le lieu {code}" }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Désactiver", Exact = true }).ClickAsync();
        await Expect(row).ToContainTextAsync("Inactif");
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Réactiver le lieu {code}" }).ClickAsync();
        await Expect(row).ToContainTextAsync("Actif");
        await BackOfficeSession.CloseSnackbarsAsync(Page);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await using var scope = Scope();
        var location = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Domain.Referential.Location>().AsNoTracking().SingleAsync(l => l.AgencyId == agencyId && l.Code == code, Ct);
        (location.Name, location.Kind, location.Address, location.IsActive).ShouldBe(("Aéroport E2E (arrivées)", LocationKind.Airport, "Route de l'aéroport", true));
    }

    [Fact]
    public async Task The_support_reads_the_journal_of_an_agency_on_the_platform_journal()
    {
        var agencyId = await BackOfficeSession.NewAgencyAsync(Host, "Journal");
        string agencyName;
        await using (var scope = Scope())
        {
            agencyName = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Agency>().Where(a => a.Id == agencyId).Select(a => a.Name).SingleAsync(Ct);
        }

        await BackOfficeSession.SignInAsync(Page, await BackOfficeSession.NewStaffAsync(Host, Roles.Support), "/admin/platform");

        // The journal of an agency leads the support outside any agency to the journals of the platform.
        await Page.GotoAsync("/admin/audit");
        await Expect(Page).ToHaveURLAsync(PlatformAudit(), new PageAssertionsToHaveURLOptions { Timeout = 30_000 });
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Journal de la plateforme");

        var agency = Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Agence" });
        await agency.ClickAsync();
        await BackOfficeSession.WaitForOpenPopoverAsync(Page);
        await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = agencyName }).ClickAsync();

        // The provisioning of an agency writes many rows at once (its chart of accounts, its knowledge base…): its office is
        // searched by type, typed once the render of the chosen agency is done (it would wipe a value typed before).
        await Expect(agency).ToHaveValueAsync(agencyName);
        var type = Page.GetByLabel("Type d'objet", new PageGetByLabelOptions { Exact = true });
        await type.FillAsync("Location");
        await type.PressAsync("Tab");
        await Expect(type).ToHaveValueAsync("Location");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Rechercher" }).ClickAsync();
        var created = Page.Locator("tr", new PageLocatorOptions { HasText = "Location" }).Filter(new LocatorFilterOptions { HasText = "entity.created" }).First;
        await Expect(created).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Expect(created.Locator("[data-audit-agency]")).ToHaveTextAsync(agencyName);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
    }

    [GeneratedRegex("/admin$")]
    private static partial Regex AdminHome();

    [GeneratedRegex("/admin/platform$")]
    private static partial Regex PlatformHome();

    [GeneratedRegex("/admin/platform/audit$")]
    private static partial Regex PlatformAudit();

    [GeneratedRegex("/admin/platform/support$")]
    private static partial Regex SupportAccounts();

    [GeneratedRegex("/account/access-denied")]
    private static partial Regex AccessDenied();

    /// <summary>The page heading is marked ready once the page is interactive and loaded.</summary>
    private Task WaitUntilReadyAsync() =>
        Expect(Page.Locator("h1[data-ready=true]")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

    private AsyncServiceScope Scope()
    {
        var scope = Host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:e2e"));
        return scope;
    }
}
