using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CarRental.Application.Identity.Contracts;
using CarRental.E2E.Tests.Support;
using CarRental.Infrastructure.Messaging;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Messaging;

/// <summary>
/// The shared WhatsApp inbox on a 10-inch tablet: a customer writes (signed webhook delivery, as Meta posts it), an agent
/// opens the inbox, sees the message, takes the conversation over, replies inside the 24-hour window (sent through the
/// fake, delivered), and hands it back to the bot — without serious or critical accessibility violation.
/// </summary>
public sealed class WhatsAppInboxTests(E2EHost host) : BrowserTest(host)
{
    protected override ViewportSize Viewport => new() { Width = 1280, Height = 800 };

    private ILocator Conversation => Page.Locator("[data-testid='conversation']");

    [Fact]
    public async Task An_agent_takes_over_a_customer_message_replies_in_the_window_and_hands_back_to_the_bot()
    {
        var waId = "3376" + RandomNumberGenerator.GetInt32(0, 10_000_000).ToString("D7", CultureInfo.InvariantCulture);
        var name = NewName();
        await PostCustomerMessageAsync(waId, name, "Bonjour, je voudrais une voiture automatique pour samedi.");
        await SignInAsync("/admin/whatsapp");

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Boîte WhatsApp");
        await Page.GetByLabel("Rechercher (numéro ou nom)").FillAsync(waId);
        var item = Page.Locator("button.whatsapp-item").Filter(new LocatorFilterOptions { HasText = name });
        await Expect(item).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Expect(item).ToContainTextAsync("1 non lu(s)");
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await item.ClickAsync();
        var thread = Conversation.Locator("[data-testid='thread']");
        await Expect(thread).ToContainTextAsync("Bonjour, je voudrais une voiture automatique pour samedi.");
        await Expect(Conversation).ToContainTextAsync("Fenêtre de 24 h");
        await Expect(Conversation.Locator("[data-mode='Bot']").First).ToBeVisibleAsync();
        await Expect(Conversation.GetByText("Le bot répond à ce client. Prenez la main pour lui écrire.")).ToBeVisibleAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await Conversation.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Prendre la main" }).ClickAsync();
        var reply = Conversation.GetByLabel("Votre réponse");
        await Expect(reply).ToBeVisibleAsync();
        await Expect(Conversation.Locator("[data-mode='Human']").First).ToBeVisibleAsync();
        await reply.FillAsync($"Bonjour {name}, nous avons une Clio automatique disponible samedi.");
        await Conversation.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Envoyer", Exact = true }).ClickAsync();

        var agentMessage = thread.Locator("[data-author='Agent']");
        await Expect(agentMessage).ToContainTextAsync("nous avons une Clio automatique");
        await Expect(agentMessage).ToHaveAttributeAsync("data-status", "Delivered", new LocatorAssertionsToHaveAttributeOptions { Timeout = 30_000 });
        await Expect(agentMessage).ToContainTextAsync("Distribué");
        Host.Factory.WhatsApp.SentTo(waId).ShouldHaveSingleItem();
        await WaitForSnackbarsAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await Conversation.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Rendre au bot" }).ClickAsync();
        await Expect(Conversation.Locator("[data-mode='Bot']").First).ToBeVisibleAsync();
        await Expect(Conversation.GetByText("Le bot répond à ce client. Prenez la main pour lui écrire.")).ToBeVisibleAsync();
        await WaitForSnackbarsAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
    }

    [Fact]
    public async Task A_new_customer_message_appears_without_reloading_the_page()
    {
        var waId = "3377" + RandomNumberGenerator.GetInt32(0, 10_000_000).ToString("D7", CultureInfo.InvariantCulture);
        var name = NewName();
        await PostCustomerMessageAsync(waId, name, "Premier message");
        await SignInAsync("/admin/whatsapp");
        await Page.GetByLabel("Rechercher (numéro ou nom)").FillAsync(waId);
        var item = Page.Locator("button.whatsapp-item").Filter(new LocatorFilterOptions { HasText = name });
        await Expect(item).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await item.ClickAsync();
        var thread = Conversation.Locator("[data-testid='thread']");
        await Expect(thread).ToContainTextAsync("Premier message");

        await PostCustomerMessageAsync(waId, name, "Second message, arrivé pendant que l'agent regarde");

        await Expect(thread).ToContainTextAsync("Second message, arrivé pendant que l'agent regarde", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
    }

    /// <summary>Posts a customer message to the webhook of the host, signed like Meta does (HMAC-SHA256 of the raw body).</summary>
    private async Task PostCustomerMessageAsync(string waId, string profileName, string text)
    {
        var payload = MetaWebhookPayloads.TextMessage(
            WhatsAppDevelopmentKeys.BusinessAccountId,
            WhatsAppDevelopmentKeys.PhoneNumberId,
            waId,
            profileName,
            $"wamid.E2E{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow,
            text);
        var body = Encoding.UTF8.GetBytes(payload);
        using var client = new HttpClient { BaseAddress = Host.BaseAddress };
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/webhooks/whatsapp", UriKind.Relative)) { Content = content };
        request.Headers.TryAddWithoutValidation(WhatsAppWebhookSecurity.SignatureHeader, WhatsAppWebhookSecurity.ComputeSignature(body, WhatsAppDevelopmentKeys.AppSecret));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task SignInAsync(string returnUrl)
    {
        var agent = await BackOfficeSession.NewStaffAsync(Host, Roles.Agent);
        await BackOfficeSession.SignInAsync(Page, agent, returnUrl);
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);
    }

    /// <summary>A WhatsApp profile name of its own (tests run in parallel and share the inbox).</summary>
    private static string NewName() => "Salma " + new string([.. Enumerable.Range(0, 6).Select(_ => (char)('a' + RandomNumberGenerator.GetInt32(26)))]);

    /// <summary>The snackbars of the actions fade out before axe measures the page.</summary>
    private Task WaitForSnackbarsAsync() => BackOfficeSession.CloseSnackbarsAsync(Page);
}
