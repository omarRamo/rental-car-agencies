using System.Net;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Identity.Users;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Identity;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Support;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CarRental.Integration.Tests.Identity;

/// <summary>
/// Extra permissions on top of the role presets (SPEC §3: « la permission Tarification peut être confiée à un agent »):
/// they apply at the next request of the user's open session, without signing in again, and stop at the next request
/// once revoked.
/// </summary>
public sealed class ExtraPermissionsTests(IntegrationHost host)
{
    private static readonly Uri Pricing = new("/admin/pricing", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_granted_pricing_management_reaches_the_pricing_screen_until_it_is_revoked()
    {
        var (agentId, agentEmail) = await TestStaff.CreateAsync(host, Roles.Agent);
        using var agent = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(agent, agentEmail, "/admin", Ct);
        await ShouldBeDeniedAsync(await agent.GetAsync(Pricing, Ct));
        var manager = await ManagerAsync();

        await SetExtraPermissionsAsync(manager, agentId, [Permissions.PricingManage]);

        // Same session: the claims are rebuilt at this very request, the MFA sign-in is kept.
        (await agent.GetAsync(Pricing, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await agent.GetAsync(new Uri("/admin/bookings", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await SetExtraPermissionsAsync(manager, agentId, []);

        await ShouldBeDeniedAsync(await agent.GetAsync(Pricing, Ct));
        (await agent.GetAsync(new Uri("/admin/bookings", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var events = await db.Set<AuditLog>()
            .Where(a => a.EntityId == agentId.ToString() && a.Action.StartsWith("identity.permission_"))
            .OrderBy(a => a.Id)
            .Select(a => new { a.Action, a.Details, a.UserId })
            .ToListAsync(Ct);
        events.Select(e => e.Action).ShouldBe([AuditActions.PermissionGranted, AuditActions.PermissionRevoked]);
        events.ShouldAllBe(e => e.Details == $"{Permissions.PricingManage} ({agentEmail})" && e.UserId == manager.UserId);
        (await db.Set<AuditLog>().CountAsync(a => a.EntityType == "UserClaim" && a.UserId == manager.UserId
            && (a.Action == AuditActions.EntityCreated || a.Action == AuditActions.EntityDeleted), Ct)).ShouldBe(2);
        (await db.UserClaims.AnyAsync(c => c.UserId == agentId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_users_list_shows_extra_permissions_apart_from_the_roles()
    {
        var (agentId, agentEmail) = await TestStaff.CreateAsync(host, Roles.Agent);
        var manager = await ManagerAsync();
        await SetExtraPermissionsAsync(manager, agentId, [Permissions.PricingManage, Permissions.BookingsManage]);

        await using var scope = host.CreateScope(manager);
        var users = scope.ServiceProvider.GetRequiredService<UserAdministrationService>();
        var listed = (await users.ListAsync(agentEmail, new CarRental.Application.Common.Paging.PageRequest(), Ct)).Value.Items.ShouldHaveSingleItem();
        var permissions = (await users.GetPermissionsAsync(agentId, Ct)).Value;

        listed.Roles.ShouldBe([Roles.Agent]);
        listed.ExtraPermissions.ShouldBe([Permissions.PricingManage]);
        permissions.ExtraPermissions.ShouldBe([Permissions.PricingManage]);
        permissions.EffectivePermissions.ShouldContain(Permissions.PricingRead);
    }

    [Fact]
    public async Task An_open_circuit_ends_when_one_of_its_permissions_is_revoked_but_not_when_one_is_granted()
    {
        var (agentId, _) = await TestStaff.CreateAsync(host, Roles.Agent);
        var manager = await ManagerAsync();
        await SetExtraPermissionsAsync(manager, agentId, [Permissions.PricingManage]);

        await using var scope = host.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value;
        var circuit = await factory.CreateAsync((await userManager.FindByIdAsync(agentId.ToString()))!);
        circuit.HasClaim(Permissions.ClaimType, Permissions.PricingRead).ShouldBeTrue("implied by pricing.manage");

        await SetExtraPermissionsAsync(manager, agentId, [Permissions.PricingManage, Permissions.ReportsRead]);
        (await SessionClaims.IsStillValidAsync(userManager, factory, circuit, options, scope.ServiceProvider.GetRequiredService<IAgencyDirectory>())).ShouldBeTrue();

        await SetExtraPermissionsAsync(manager, agentId, [Permissions.ReportsRead]);
        (await SessionClaims.IsStillValidAsync(userManager, factory, circuit, options, scope.ServiceProvider.GetRequiredService<IAgencyDirectory>())).ShouldBeFalse();
    }

    private static async Task ShouldBeDeniedAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync(Ct));
        HttpSignIn.PathAndQuery(response.Headers.Location!).ShouldContain("/account/access-denied");
    }

    private async Task SetExtraPermissionsAsync(UserContextSnapshot manager, Guid userId, string[] permissions)
    {
        await using var scope = host.CreateScope(manager);
        var result = await scope.ServiceProvider.GetRequiredService<UserAdministrationService>().SetExtraPermissionsAsync(userId, permissions, Ct);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
    }

    private async Task<UserContextSnapshot> ManagerAsync()
    {
        var (id, email) = await TestStaff.CreateAsync(host, Roles.Manager);
        return await TestStaff.ActorAsync(host, id, email, Roles.Manager);
    }
}
