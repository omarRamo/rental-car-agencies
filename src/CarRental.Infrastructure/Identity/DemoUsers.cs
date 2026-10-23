using CarRental.Application.Common.Seeding;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence;
using CarRental.Infrastructure.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Identity;

/// <summary>
/// Demo accounts (development and recette only; <c>Seed:Demo=true</c>). They share the configured password and TOTP key.
/// The staff accounts belong to a demo agency (by its name, see the Referential demo seeder); the support account to none.
/// </summary>
public static class DemoUsers
{
    /// <summary>Main demo agency (created by the Referential demo seeder).</summary>
    public const string DemoAgencyName = "Agence Démo Tunis";

    /// <summary>Second demo agency, to show that the agencies never see each other (ADR-0067).</summary>
    public const string SecondDemoAgencyName = "Agence Démo Sousse";

    public const string ManagerEmail = "gerant@demo.local";
    public const string AgentEmail = "agent@demo.local";
    public const string AccountantEmail = "comptable@demo.local";
    public const string SecondAgencyManagerEmail = "gerant.sousse@demo.local";
    public const string SupportEmail = "support@demo.local";

    /// <summary>Every demo account: its e-mail, display name, role and agency name (<see langword="null"/> for the support).</summary>
    public static IReadOnlyList<(string Email, string DisplayName, string Role, string? Agency)> All { get; } =
    [
        (ManagerEmail, "Gérant Démo", Roles.Manager, DemoAgencyName),
        (AgentEmail, "Agent Démo", Roles.Agent, DemoAgencyName),
        (AccountantEmail, "Comptable Démo", Roles.Accountant, DemoAgencyName),
        (SecondAgencyManagerEmail, "Gérant Sousse", Roles.Manager, SecondDemoAgencyName),
        (SupportEmail, "Support Démo", Roles.Support, null),
    ];
}

/// <summary>
/// Creates the demo users with two-factor authentication already enabled on a known authenticator key, so that
/// developers and automated tests can sign in. Skipped (with a warning) when the password or key is not configured; a
/// staff account whose agency does not exist yet is skipped too.
/// </summary>
internal sealed partial class IdentityDemoSeeder(
    UserManager<ApplicationUser> userManager,
    IUserStore<ApplicationUser> userStore,
    CarRentalDbContext db,
    IOptions<SeedOptions> options,
    IOptions<TwoFactorOptions> twoFactor,
    TimeProvider timeProvider,
    ILogger<IdentityDemoSeeder> logger) : IDataSeeder
{
    public SeedStage Stage => SeedStage.Demo;

    public int Order => 300;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.DemoUserPassword) || string.IsNullOrWhiteSpace(settings.DemoAuthenticatorKey))
        {
            LogMissingSecrets();
            return;
        }

        var agencies = await db.Set<Agency>()
            .AsNoTracking()
            .Where(a => a.Name == DemoUsers.DemoAgencyName || a.Name == DemoUsers.SecondDemoAgencyName)
            .ToDictionaryAsync(a => a.Name, a => a.Id, cancellationToken);
        var keyStore = (IUserAuthenticatorKeyStore<ApplicationUser>)userStore;
        foreach (var (email, displayName, role, agencyName) in DemoUsers.All)
        {
            Guid? agencyId = null;
            if (agencyName is not null)
            {
                if (!agencies.TryGetValue(agencyName, out var id))
                {
                    LogMissingAgency(email, agencyName);
                    continue;
                }

                agencyId = id;
            }

            var user = await userManager.FindByEmailAsync(email);
            if (user is not null)
            {
                if (user.AgencyId != agencyId)
                {
                    // An account created before the agencies were separated (ADR-0067) joins its demo agency.
                    user.AgencyId = agencyId;
                    user.SelectedAgencyId = null;
                    Ensure(await userManager.UpdateAsync(user), email);
                }

                if (!await userManager.IsInRoleAsync(user, role))
                {
                    Ensure(await userManager.AddToRoleAsync(user, role), email);
                }

                continue;
            }

            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = displayName,
                LockoutEnabled = true,
                CreatedAtUtc = timeProvider.GetUtcNow(),
                AgencyId = agencyId,
            };
            Ensure(await userManager.CreateAsync(user, settings.DemoUserPassword), email);
            Ensure(await userManager.AddToRoleAsync(user, role), email);
            if (twoFactor.Value.Required)
            {
                await keyStore.SetAuthenticatorKeyAsync(user, settings.DemoAuthenticatorKey, cancellationToken);
                Ensure(await userManager.SetTwoFactorEnabledAsync(user, true), email);
            }

            LogCreated(email, role);
        }
    }

    private static void Ensure(IdentityResult result, string email)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Demo user {email} could not be created: {string.Join(" ", result.Errors.Select(e => e.Description))}");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seed:Demo is on but Seed:DemoUserPassword or Seed:DemoAuthenticatorKey is missing: demo users are not created.")]
    private partial void LogMissingSecrets();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo user {Email} skipped: the demo agency {Agency} does not exist.")]
    private partial void LogMissingAgency(string email, string agency);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo user {Email} ({Role}) created.")]
    private partial void LogCreated(string email, string role);
}
