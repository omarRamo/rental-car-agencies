using System.Text.RegularExpressions;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Common.Tenancy;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Identity;
using CarRental.Testing;
using Deque.AxeCore.Playwright;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Support;

/// <summary>Back office helpers of the browser tests: staff accounts of their own, sign-in with TOTP, axe checks.</summary>
internal static class BackOfficeSession
{
    /// <summary>
    /// A new staff account of <paramref name="role"/> with two-factor authentication on the test key: a member of
    /// <paramref name="agencyId"/> (the main demo agency by default), or a support account, of no agency, for the role
    /// <c>Support</c> (ADR-0067).
    /// </summary>
    public static async Task<string> NewStaffAsync(E2EHost host, string role, Guid? agencyId = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@tests.local";
        await using var scope = host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:e2e"));
        Guid? agency = role == Roles.Support ? null : agencyId ?? await DemoAgencyIdAsync(scope.ServiceProvider);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = $"{role} E2E",
            LockoutEnabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            AgencyId = agency,
        };
        (await users.CreateAsync(user, TestAccounts.Password)).Succeeded.ShouldBeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
        var keys = (IUserAuthenticatorKeyStore<ApplicationUser>)scope.ServiceProvider.GetRequiredService<IUserStore<ApplicationUser>>();
        await keys.SetAuthenticatorKeyAsync(user, TestAccounts.AuthenticatorKey, CancellationToken.None);
        (await users.SetTwoFactorEnabledAsync(user, true)).Succeeded.ShouldBeTrue();
        return email;
    }

    /// <summary>
    /// A new agency of its own (default modules, its office location provisioned like an agency registered by the
    /// support), named after <paramref name="label"/> so that it sorts after the demo agencies; returns its id.
    /// </summary>
    public static async Task<Guid> NewAgencyAsync(E2EHost host, string label)
    {
        ArgumentNullException.ThrowIfNull(host);
        await using var scope = host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:e2e"));
        var agency = Agency.Register(new AgencyDetails($"Agence E2E {label} {Guid.NewGuid().ToString("N")[..12]}", "Bizerte")).Value;
        scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Agency>().Add(agency);
        await scope.ServiceProvider.GetRequiredService<IAppDbContext>().SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<AgencyProvisioning>().ProvisionAsync(agency.Id, CancellationToken.None);
        return agency.Id;
    }

    /// <summary>Signs in with password and TOTP and waits for <paramref name="returnUrl"/>.</summary>
    public static async Task SignInAsync(IPage page, string email, string returnUrl)
    {
        ArgumentNullException.ThrowIfNull(page);
        await page.GotoAsync($"/account/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
        await page.Locator("#email").FillAsync(email);
        await page.Locator("#password").FillAsync(TestAccounts.Password);
        await page.Locator("button[type=submit]").ClickAsync();
        await page.Locator("#code").FillAsync(Totp.Compute(TestAccounts.AuthenticatorKey));
        await page.Locator("button[type=submit]").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex(Regex.Escape(returnUrl) + "$"));
    }

    /// <summary>Waits until the circuit renders the page (the admin layout marks its main element), so that clicks work.</summary>
    public static async Task WaitUntilInteractiveAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        await Expect(page.Locator("main#admin-main[data-interactive='true']")).ToBeAttachedAsync(new LocatorAssertionsToBeAttachedOptions { Timeout = 30_000 });
    }

    /// <summary>
    /// Waits until the open MudBlazor popover (picker, select list, menu) has finished its fade-in: axe would compute the
    /// contrast of half-transparent text.
    /// </summary>
    public static async Task WaitForOpenPopoverAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        await page.WaitForFunctionAsync(
            "() => { const open = [...document.querySelectorAll('.mud-popover-open')]; return open.length > 0 && open.every(p => getComputedStyle(p).opacity === '1'); }");
    }

    /// <summary>Waits until the open dialog has finished its opening animation (same reason as <see cref="WaitForOpenPopoverAsync"/>).</summary>
    public static async Task WaitForOpenDialogAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        await page.WaitForFunctionAsync(
            "() => { const dialogs = [...document.querySelectorAll('.mud-dialog')]; return dialogs.length > 0 && dialogs.every(d => getComputedStyle(d).opacity === '1' && d.getAnimations().every(a => a.playState === 'finished')); }");
    }

    /// <summary>No critical or serious axe violation on the page, once its transitions have ended.</summary>
    public static async Task AssertNoSeriousAxeViolationAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        await WaitForTransitionsAsync(page);
        var result = await page.RunAxe();
        var blocking = result.Violations
            .Where(v => v.Impact is "critical" or "serious")
            .Select(v => $"{v.Id} ({v.Impact}): {v.Help} [{string.Join(", ", v.Nodes.Select(n => n.Html.Length > 300 ? n.Html[..300] : n.Html))}]")
            .ToList();
        blocking.ShouldBeEmpty();
    }

    /// <summary>
    /// Waits, at most 5 seconds, until no finite animation or transition runs on the page: axe would otherwise measure the
    /// contrast of a text or a button half-way through its fade (a false color-contrast violation). Endless animations,
    /// such as progress indicators, are ignored.
    /// </summary>
    public static async Task WaitForTransitionsAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        try
        {
            await page.WaitForFunctionAsync(
                "() => document.getAnimations().every(a => a.playState !== 'running' || a.effect?.getComputedTiming().iterations === Infinity)",
                null,
                new PageWaitForFunctionOptions { Timeout = 5_000 });
        }
        catch (PlaywrightException)
        {
            // Something keeps animating: the page is checked as it is.
        }
    }

    /// <summary>
    /// Closes the snackbars shown and waits until none is left. Waiting alone is not enough: a snackbar under the mouse
    /// pointer keeps itself open (MudBlazor pauses its timer while hovered).
    /// </summary>
    public static async Task CloseSnackbarsAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        foreach (var close in await page.Locator(".mud-snackbar button").AllAsync())
        {
            try
            {
                await close.ClickAsync(new LocatorClickOptions { Timeout = 2_000 });
            }
            catch (PlaywrightException)
            {
                // Gone by itself meanwhile (TimeoutException derives from PlaywrightException).
            }
        }

        await Expect(page.Locator(".mud-snackbar")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 20_000 });
    }

    /// <summary>The main demo agency (its demo accounts belong to it).</summary>
    private static Task<Guid> DemoAgencyIdAsync(IServiceProvider services) =>
        services.GetRequiredService<IAppDbContext>().Set<Agency>()
            .Where(a => a.Name == DemoUsers.DemoAgencyName)
            .Select(a => a.Id)
            .SingleAsync();
}
