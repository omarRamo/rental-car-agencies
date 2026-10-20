using System.Text.RegularExpressions;
using CarRental.Application.Common.Security;
using CarRental.Infrastructure.Identity;
using CarRental.Testing;
using Deque.AxeCore.Playwright;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Support;

/// <summary>Back office helpers of the browser tests: staff accounts of their own, sign-in with TOTP, axe checks.</summary>
internal static class BackOfficeSession
{
    /// <summary>A new staff account of <paramref name="role"/> with two-factor authentication on the test key.</summary>
    public static async Task<string> NewStaffAsync(E2EHost host, string role)
    {
        ArgumentNullException.ThrowIfNull(host);
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@tests.local";
        await using var scope = host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:e2e"));
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = $"{role} E2E",
            LockoutEnabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        (await users.CreateAsync(user, TestAccounts.Password)).Succeeded.ShouldBeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
        var keys = (IUserAuthenticatorKeyStore<ApplicationUser>)scope.ServiceProvider.GetRequiredService<IUserStore<ApplicationUser>>();
        await keys.SetAuthenticatorKeyAsync(user, TestAccounts.AuthenticatorKey, CancellationToken.None);
        (await users.SetTwoFactorEnabledAsync(user, true)).Succeeded.ShouldBeTrue();
        return email;
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

    /// <summary>No critical or serious axe violation on the page as it is now.</summary>
    public static async Task AssertNoSeriousAxeViolationAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var result = await page.RunAxe();
        var blocking = result.Violations
            .Where(v => v.Impact is "critical" or "serious")
            .Select(v => $"{v.Id} ({v.Impact}): {v.Help} [{string.Join(", ", v.Nodes.Select(n => n.Html.Length > 300 ? n.Html[..300] : n.Html))}]")
            .ToList();
        blocking.ShouldBeEmpty();
    }
}
