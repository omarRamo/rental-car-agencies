using System.Text.RegularExpressions;
using CarRental.Application.Identity.Contracts;
using CarRental.E2E.Tests.Support;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests;

/// <summary>Administration screens of the manager on a 10-inch tablet: background messages, users and audit log.</summary>
public sealed partial class AdministrationScreensTests(E2EHost host) : BrowserTest(host)
{
    protected override ViewportSize Viewport => new() { Width = 1280, Height = 800 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_manager_finds_a_failed_message_and_retries_it()
    {
        var messageId = await InsertFailedMessageAsync();
        await BackOfficeSession.SignInAsync(Page, await BackOfficeSession.NewStaffAsync(Host, Roles.Manager), "/admin/outbox");
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);

        var row = Page.Locator("tr", new PageLocatorOptions { HasText = "E2eUnhandledMessage" });
        await Expect(row).ToContainTextAsync("En échec", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(row).ToContainTextAsync("System.TimeoutException: relay unavailable");
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Afficher l'erreur complète" }).ClickAsync();
        await Expect(Page.Locator("pre", new PageLocatorOptions { HasText = "at Relay.Send()" })).ToBeVisibleAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameRegex = RetryLabel() }).ClickAsync();
        await Expect(Page.GetByText("Message relancé")).ToBeVisibleAsync();
        await WaitUntilProcessedAsync(messageId);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Actualiser" }).ClickAsync();
        await Expect(row).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task The_users_and_audit_screens_pass_axe_with_their_accessible_pagers()
    {
        await BackOfficeSession.SignInAsync(Page, await BackOfficeSession.NewStaffAsync(Host, Roles.Manager), "/admin/users");
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);
        await Expect(Page.Locator("table tbody tr").First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Expect(Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Lignes par page" })).ToBeVisibleAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Journal d'audit" }).ClickAsync();
        await Expect(Page.Locator("tr", new PageLocatorOptions { HasText = "identity.login_succeeded" }).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        // An empty date filter opened: its calendar passes too.
        await Page.GetByLabel("Du", new PageGetByLabelOptions { Exact = true }).ClickAsync();
        await Expect(Page.Locator(".mud-picker-content")).ToBeVisibleAsync();
        await BackOfficeSession.WaitForOpenPopoverAsync(Page);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
    }

    [Fact]
    public async Task A_manager_edits_settings_of_every_kind_and_overrides_one_for_the_agency()
    {
        await BackOfficeSession.SignInAsync(Page, await BackOfficeSession.NewStaffAsync(Host, Roles.Manager), "/admin/settings");
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Flotte", Level = 2 })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Agence" })).ToHaveTextAsync("Agence Démo Tunis");
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        // A setting without label resources shows its key; a text is edited in a text field.
        var motto = SettingRow("tests.motto");
        await motto.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Modifier la valeur de la plateforme : tests.motto" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await dialog.GetByLabel("Valeur").FillAsync("Bonjour E2E");
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Enregistrer" }).ClickAsync();
        await Expect(Page.GetByText("Paramètre enregistré.")).ToBeVisibleAsync();
        await Expect(motto.Locator("[data-setting-scope='platform']")).ToContainTextAsync("Bonjour E2E");

        // A time range, typed in its two time fields.
        var hours = SettingRow("tests.opening_hours");
        await hours.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Modifier la valeur de la plateforme : tests.opening_hours" }).ClickAsync();
        await dialog.GetByLabel("Début").FillAsync("09:00");
        await dialog.GetByLabel("Début").PressAsync("Tab");
        await dialog.GetByLabel("Fin").FillAsync("19:30");
        await dialog.GetByLabel("Fin").PressAsync("Tab");
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Enregistrer" }).ClickAsync();
        await Expect(hours.Locator("[data-setting-scope='platform']")).ToContainTextAsync("09:00 – 19:30");

        // A platform-only setting offers no agency value.
        var retention = SettingRow("outbox.processed_retention");
        await Expect(retention.Locator("[data-setting-scope='agency']")).ToContainTextAsync("Même valeur pour toutes les agences");
        await Expect(retention.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameRegex = AgencyValueLabel() })).ToHaveCountAsync(0);

        // The preparation buffer of the agency: an own value, then back to the platform value.
        var buffer = SettingRow("fleet.preparation_buffer");
        await Expect(buffer.Locator("[data-setting-scope='agency']")).ToContainTextAsync("Valeur de la plateforme");
        await buffer.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Définir la valeur de l'agence : Tampon de préparation" }).ClickAsync();
        await dialog.GetByLabel("Durée").FillAsync("3");
        await dialog.GetByLabel("Durée").PressAsync("Tab");
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Enregistrer" }).ClickAsync();
        await Expect(buffer.Locator("[data-setting-scope='agency']")).ToContainTextAsync("3 h");

        await buffer.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retirer la valeur de l'agence : Tampon de préparation" }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retirer", Exact = true }).ClickAsync();
        await Expect(Page.GetByText("Valeur de l'agence retirée")).ToBeVisibleAsync();
        await Expect(buffer.Locator("[data-setting-scope='agency']")).ToContainTextAsync("Valeur de la plateforme");
    }

    [Fact]
    public async Task A_manager_gives_an_agent_an_extra_permission_and_takes_it_back()
    {
        var agentEmail = await BackOfficeSession.NewStaffAsync(Host, Roles.Agent);
        await BackOfficeSession.SignInAsync(Page, await BackOfficeSession.NewStaffAsync(Host, Roles.Manager), "/admin/users");
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);
        await Page.GetByLabel("Rechercher (nom ou e-mail)").FillAsync(agentEmail);
        var row = Page.Locator("tr", new PageLocatorOptions { Has = Page.Locator($"[data-user-email='{agentEmail}']") });
        await Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Expect(row.Locator("[data-extra-permissions]")).ToHaveTextAsync("—");

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Permissions de Agent E2E" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = "Dossiers (créer, modifier) (rôle)" })).ToBeDisabledAsync();
        var pricing = dialog.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = "Tarifs, saisons et promotions (gérer)", Exact = true });
        await pricing.CheckAsync();
        await Expect(dialog.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = "Tarifs (consulter) (incluse)" })).ToBeCheckedAsync();
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Enregistrer" }).ClickAsync();

        await Expect(Page.GetByText("Permissions enregistrées")).ToBeVisibleAsync();
        await Expect(row.Locator("[data-extra-permissions]")).ToContainTextAsync("Tarifs, saisons et promotions (gérer)");

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Permissions de Agent E2E" }).ClickAsync();
        await Expect(pricing).ToBeCheckedAsync();
        await pricing.UncheckAsync();
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Enregistrer" }).ClickAsync();
        await Expect(row.Locator("[data-extra-permissions]")).ToHaveTextAsync("—");
    }

    [GeneratedRegex("^Relancer le message E2eUnhandledMessage")]
    private static partial Regex RetryLabel();

    [GeneratedRegex("valeur de l'agence")]
    private static partial Regex AgencyValueLabel();

    /// <summary>The table row of a setting, found by its key.</summary>
    private ILocator SettingRow(string key) =>
        Page.Locator("tr", new PageLocatorOptions { Has = Page.Locator($"[data-setting-key='{key}']") });

    /// <summary>A message without handler that failed for good (written in one insert: the processor never sees it half-made).</summary>
    private async Task<Guid> InsertFailedMessageAsync()
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var message = OutboxMessage.Create("CarRental.Tests.E2eUnhandledMessage", "{}", DateTimeOffset.UtcNow.AddHours(-3), "corr-e2e");
        var entry = db.Add(message);
        entry.Property(m => m.AttemptCount).CurrentValue = 10;
        entry.Property(m => m.LastError).CurrentValue = "System.TimeoutException: relay unavailable\n   at Relay.Send()";
        await db.SaveChangesAsync(Ct);
        return message.Id;
    }

    private async Task WaitUntilProcessedAsync(Guid messageId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (true)
        {
            await using (var scope = Host.Factory.Services.CreateAsyncScope())
            {
                var processed = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<OutboxMessage>()
                    .AnyAsync(m => m.Id == messageId && m.ProcessedAtUtc != null, Ct);
                if (processed)
                {
                    return;
                }
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, "the retried message should be processed");
            await Task.Delay(200, Ct);
        }
    }
}
