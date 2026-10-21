using System.Globalization;
using System.Security.Cryptography;
using CarRental.Application.Identity.Contracts;
using CarRental.E2E.Tests.Support;
using CarRental.Infrastructure.Messaging.Agent;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Not "…Messaging.Agent": the namespace would hide members named Agent from the other messaging tests.
namespace CarRental.E2E.Tests.Messaging.AiAgent;

/// <summary>
/// The WhatsApp assistant in the back office: a manager writes an answer of the knowledge base and validates it (the
/// assistant gives validated answers only); a customer is answered by the bot (scripted model) and an agent takes the
/// conversation over in the shared inbox — without serious or critical accessibility violation.
/// </summary>
public sealed class WhatsAppAgentTests(E2EHost host) : BrowserTest(host)
{
    protected override ViewportSize Viewport => new() { Width = 1280, Height = 800 };

    [Fact]
    public async Task A_manager_writes_an_answer_of_the_knowledge_base_then_validates_it()
    {
        var suffix = Letters(6);
        var title = $"Parking {suffix}";
        await SignInAsync(Roles.Manager, "/admin/whatsapp/knowledge");

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Base de connaissances de l'assistant WhatsApp");
        var table = Page.Locator("[data-testid='knowledge']");
        await Expect(table).ToContainTextAsync("documents", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Nouvelle réponse" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await dialog.GetByLabel("Clé du sujet").FillAsync($"parking_{suffix}");
        await dialog.GetByLabel("Titre").FillAsync(title);
        await dialog.GetByLabel("Mots-clés").FillAsync("parking, stationnement");
        await dialog.GetByLabel("Réponse en français").FillAsync("Le parking de l'agence est gratuit pour nos clients.");
        await dialog.GetByLabel("Réponse en anglais").FillAsync("The agency car park is free for our customers.");
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Enregistrer" }).ClickAsync();

        await Expect(dialog).ToBeHiddenAsync();
        var row = table.Locator("tr").Filter(new LocatorFilterOptions { HasText = title });
        await Expect(row.Locator("[data-state]")).ToHaveAttributeAsync("data-state", "draft");
        await Expect(row).ToContainTextAsync("Brouillon : l'assistant ne la donne pas");
        await WaitForSnackbarsAsync();

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Valider la réponse « {title} »" }).ClickAsync();

        await Expect(row.Locator("[data-state]")).ToHaveAttributeAsync("data-state", "validated");
        await Expect(row).ToContainTextAsync("Validée par");
        await WaitForSnackbarsAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
    }

    [Fact]
    public async Task The_bot_answers_a_customer_then_an_agent_takes_the_conversation_over()
    {
        var waId = "3378" + RandomNumberGenerator.GetInt32(0, 10_000_000).ToString("D7", CultureInfo.InvariantCulture);
        var name = "Nadia " + Letters(6);
        Host.Factory.Agent.Script(waId, ScriptedResponse.Reply("Bonjour ! Je suis l'assistant de l'agence. Pour quelles dates et quel lieu ?"));
        await Host.Factory.WhatsApp.SimulateIncomingTextAsync(waId, name, "Bonjour, je voudrais louer une voiture", TestContext.Current.CancellationToken);
        await SignInAsync(Roles.Agent, "/admin/whatsapp");

        await Page.GetByLabel("Rechercher (numéro ou nom)").FillAsync(waId);
        var item = Page.Locator("button.whatsapp-item").Filter(new LocatorFilterOptions { HasText = name });
        await Expect(item).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await item.ClickAsync();
        var conversation = Page.Locator("[data-testid='conversation']");
        var thread = conversation.Locator("[data-testid='thread']");
        await Expect(thread.Locator("[data-author='Bot']")).ToContainTextAsync("Je suis l'assistant de l'agence", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(conversation.Locator("[data-mode='Bot']").First).ToBeVisibleAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await conversation.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Prendre la main" }).ClickAsync();
        await Expect(conversation.Locator("[data-mode='Human']").First).ToBeVisibleAsync();
        var reply = conversation.GetByLabel("Votre réponse");
        await reply.FillAsync($"Bonjour {name}, ici l'agence : je prends le relais.");
        await conversation.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Envoyer", Exact = true }).ClickAsync();

        var agentMessage = thread.Locator("[data-author='Agent']");
        await Expect(agentMessage).ToContainTextAsync("je prends le relais");
        await Expect(agentMessage).ToHaveAttributeAsync("data-status", "Delivered", new LocatorAssertionsToHaveAttributeOptions { Timeout = 30_000 });
        Host.Factory.WhatsApp.SentTo(waId).Count.ShouldBe(2, "the reply of the bot, then the agent's");
        Host.Factory.Agent.RequestsFor(waId).Count.ShouldBe(1, "the bot is silent once an agent took over");
        await WaitForSnackbarsAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
    }

    [Fact]
    public async Task The_costs_of_the_assistant_are_shown_by_month()
    {
        await SignInAsync(Roles.Manager, "/admin/whatsapp/costs");

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Coûts de l'assistant WhatsApp");
        await Expect(Page.Locator("[data-testid='agent-costs'] tbody tr")).ToHaveCountAsync(12);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
    }

    private async Task SignInAsync(string role, string returnUrl)
    {
        var staff = await BackOfficeSession.NewStaffAsync(Host, role);
        await BackOfficeSession.SignInAsync(Page, staff, returnUrl);
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);
    }

    private static string Letters(int count) => new([.. Enumerable.Range(0, count).Select(_ => (char)('a' + RandomNumberGenerator.GetInt32(26)))]);

    /// <summary>The snackbars of the actions fade out before axe measures the page.</summary>
    private Task WaitForSnackbarsAsync() =>
        Expect(Page.Locator(".mud-snackbar")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 20_000 });
}
