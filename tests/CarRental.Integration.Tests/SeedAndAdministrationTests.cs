using CarRental.Application.Identity;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Identity.Users;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Identity;
using CarRental.Integration.Tests.Support;
using CarRental.Infrastructure.Persistence;
using CarRental.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests;

public sealed class SeedAndAdministrationTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Demo_referential_data_and_default_settings_are_seeded()
    {
        await using var scope = host.CreateScope();
        var referential = scope.ServiceProvider.GetRequiredService<IReferentialQueries>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsReader>();

        var agencies = await referential.GetAgenciesAsync(Ct);
        var tunis = agencies.Single(a => a.Name == "Agence Démo Tunis");
        var sousse = agencies.Single(a => a.Name == "Agence Démo Sousse");
        var codes = (await referential.GetLocationsAsync(tunis.Id, Ct)).Select(l => l.Code).ToList();
        codes.ShouldBe(["AGC", "TUN", "NBE"], ignoreOrder: true);
        (await referential.GetLocationsAsync(sousse.Id, Ct)).Select(l => l.Code).ShouldBe(["SOU", "MIR"], ignoreOrder: true);
        (await referential.FindLocationByCodeAsync(tunis.Id, "tun", Ct))!.Kind.ShouldBe(LocationKindInfo.Airport);
        (await referential.FindLocationByCodeAsync(sousse.Id, "tun", Ct)).ShouldBeNull("a code is looked up in one agency");

        (await settings.GetAsync(WellKnownSettings.BookingHoldDuration, cancellationToken: Ct)).ShouldBe(TimeSpan.FromHours(24));
        (await settings.GetAsync(WellKnownSettings.PreparationBuffer, cancellationToken: Ct)).ShouldBe(TimeSpan.FromHours(2));
        (await settings.GetAsync(WellKnownSettings.LateReturnTolerance, cancellationToken: Ct)).ShouldBe(TimeSpan.FromMinutes(59));
        (await settings.GetAsync(WellKnownSettings.VatRate, cancellationToken: Ct)).ShouldBe(0.19m);
        (await settings.GetAsync(WellKnownSettings.DailyContributionExclVat, cancellationToken: Ct)).Amount.ShouldBe(2m);
        (await settings.GetAsync(WellKnownSettings.StampDuty, cancellationToken: Ct)).IsZero.ShouldBeTrue();
    }

    [Fact]
    public async Task Roles_hold_exactly_their_preset_permissions()
    {
        await using var scope = host.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        foreach (var roleName in Roles.All)
        {
            var role = await roles.FindByNameAsync(roleName);
            role.ShouldNotBeNull(roleName);
            var permissions = (await roles.GetClaimsAsync(role)).Where(c => c.Type == Permissions.ClaimType).Select(c => c.Value);
            permissions.ShouldBe(RolePresets.PermissionsOf(roleName), ignoreOrder: true);
        }
    }

    [Fact]
    public async Task Demo_users_have_their_role_and_two_factor_enabled()
    {
        await using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        foreach (var (email, _, role, _) in DemoUsers.All)
        {
            var user = await users.FindByEmailAsync(email);
            user.ShouldNotBeNull(email);
            (await users.IsInRoleAsync(user, role)).ShouldBeTrue();
            (await users.GetTwoFactorEnabledAsync(user)).ShouldBeTrue();
            (await users.GetAuthenticatorKeyAsync(user)).ShouldBe(TestAccounts.AuthenticatorKey);
        }
    }

    [Fact]
    public async Task A_manager_creates_a_user_who_receives_an_invitation()
    {
        var email = $"new-agent-{Guid.NewGuid():N}@demo.local";
        Guid managerId;
        await using (var scope = host.CreateScope())
        {
            managerId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(TestAccounts.ManagerEmail))!.Id;
        }

        var manager = await TestStaff.ActorAsync(host, managerId, TestAccounts.ManagerEmail, Roles.Manager, "203.0.113.9");
        Guid newUserId;
        await using (var scope = host.CreateScope(manager))
        {
            scope.ServiceProvider.GetRequiredService<CarRental.Web.Security.RequestOrigin>().BaseUri = "https://staff.example.test/";
            var administration = scope.ServiceProvider.GetRequiredService<UserAdministrationService>();
            var created = await administration.CreateUserAsync(new CreateUserRequest(email, "Nouvel Agent", Roles.Agent), Ct);
            created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.ToString() : null);
            newUserId = created.Value;

            (await administration.CreateUserAsync(new CreateUserRequest(email, "Doublon", Roles.Agent), Ct)).Error.ShouldBe(IdentityErrors.EmailTaken);
        }

        await using (var scope = host.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(newUserId.ToString());
            user.ShouldNotBeNull();
            (await users.IsInRoleAsync(user, Roles.Agent)).ShouldBeTrue();
            (await users.HasPasswordAsync(user)).ShouldBeFalse();

            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            var audit = await db.Set<AuditLog>().SingleAsync(a => a.Action == AuditActions.UserCreated && a.EntityId == newUserId.ToString(), Ct);
            audit.UserName.ShouldBe(TestAccounts.ManagerEmail);
            audit.Details.ShouldBe($"{email} (Agent)");
        }

        var invitation = await WaitForEmailAsync(email);
        invitation.ShouldContain("https://staff.example.test/account/reset-password?code=");
        invitation.ShouldContain("invitation=true");
    }

    [Fact]
    public async Task A_manager_locks_and_unlocks_a_user_and_both_are_audited()
    {
        var email = $"lock-{Guid.NewGuid():N}@demo.local";
        Guid userId;
        Guid managerId;
        await using (var scope = host.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var demoManager = (await users.FindByEmailAsync(TestAccounts.ManagerEmail))!;
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = "À verrouiller",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                AgencyId = demoManager.AgencyId,
            };
            (await users.CreateAsync(user)).Succeeded.ShouldBeTrue();
            await users.AddToRoleAsync(user, Roles.Agent);
            userId = user.Id;
            managerId = demoManager.Id;
        }

        var manager = await TestStaff.ActorAsync(host, managerId, TestAccounts.ManagerEmail, Roles.Manager);
        await using (var scope = host.CreateScope(manager))
        {
            var administration = scope.ServiceProvider.GetRequiredService<UserAdministrationService>();
            (await administration.LockAsync(userId, Ct)).IsSuccess.ShouldBeTrue();
            (await administration.ListAsync(email, new CarRental.Application.Common.Paging.PageRequest(), Ct)).Value.Items.ShouldHaveSingleItem().IsLockedOut.ShouldBeTrue();
            (await administration.UnlockAsync(userId, Ct)).IsSuccess.ShouldBeTrue();
            (await administration.LockAsync(managerId, Ct)).Error.ShouldBe(IdentityErrors.CannotLockSelf);
        }

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var actions = await db.Set<AuditLog>().Where(a => a.EntityId == userId.ToString() && a.Action.StartsWith("identity.user_")).Select(a => a.Action).ToListAsync(Ct);
        actions.ShouldBe([AuditActions.UserLocked, AuditActions.UserUnlocked], ignoreOrder: true);
        (await db.Set<AuditLog>().AnyAsync(a => a.EntityType == "User" && a.EntityId == userId.ToString() && a.Action == AuditActions.EntityUpdated && a.After!.Contains("LockoutEnd"), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Identity_queries_resolve_display_names()
    {
        await using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var manager = await users.FindByEmailAsync(TestAccounts.ManagerEmail);

        var names = await scope.ServiceProvider.GetRequiredService<IIdentityQueries>().GetDisplayNamesAsync([manager!.Id], Ct);

        names[manager.Id].ShouldBe("Gérant Démo");
    }

    private async Task<string> WaitForEmailAsync(string recipient)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (true)
        {
            if (Directory.Exists(host.Factory.EmailDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(host.Factory.EmailDirectory, "*.eml"))
                {
                    var content = await File.ReadAllTextAsync(file, Ct);
                    if (content.Contains($"<{recipient}>", StringComparison.Ordinal) || content.Contains($"To: {recipient}", StringComparison.Ordinal))
                    {
                        return DecodeBodies(content);
                    }
                }
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, $"No e-mail to {recipient}.");
            await Task.Delay(200, Ct);
        }
    }

    /// <summary>Concatenates the decoded base64 parts of the .eml file.</summary>
    private static string DecodeBodies(string eml)
    {
        var decoded = new System.Text.StringBuilder();
        foreach (var part in eml.Split("Content-Transfer-Encoding: base64", StringSplitOptions.None).Skip(1))
        {
            var body = part.Split("--=_carrental_", StringSplitOptions.None)[0];
            var base64 = string.Concat(body.Split('\n').Select(l => l.Trim()));
            decoded.Append(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
        }

        return decoded.ToString();
    }
}
