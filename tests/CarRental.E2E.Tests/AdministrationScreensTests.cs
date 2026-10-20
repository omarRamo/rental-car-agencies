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

    [GeneratedRegex("^Relancer le message E2eUnhandledMessage")]
    private static partial Regex RetryLabel();

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
