using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Identity.Users;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Identity;

namespace CarRental.Application.Tests.Identity;

public sealed class UserAdministrationServiceTests
{
    private readonly FakeUserDirectory _directory = new();
    private readonly RecordingInvitationSender _invitations = new();
    private readonly RecordingAuditTrail _audit = new();
    private readonly InlineUnitOfWork _unitOfWork = new();
    private readonly RecordingUserSessions _sessions = new();
    private readonly Guid _managerId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public UserAdministrationServiceTests()
    {
        _managerId = _directory.Add("gerant@demo.local", Roles.Manager).Id;
    }

    private UserAdministrationService ServiceFor(IUserContext context) => new(_directory, _invitations, _audit, context, _unitOfWork, _sessions);

    private UserAdministrationService AsManager() => ServiceFor(TestUsers.ContextFor(_managerId, "gerant@demo.local", Roles.Manager));

    private UserAdministrationService AsAccountant() =>
        ServiceFor(TestUsers.ContextFor(Guid.CreateVersion7(), "comptable@demo.local", Roles.Accountant));

    [Fact]
    public async Task Only_users_with_users_manage_can_administer()
    {
        var service = AsAccountant();

        (await service.ListAsync(null, new PageRequest(), Ct)).Error.ShouldBe(IdentityErrors.Forbidden);
        (await service.CreateUserAsync(new CreateUserRequest("x@demo.local", "X", Roles.Agent), Ct)).Error.ShouldBe(IdentityErrors.Forbidden);
        (await service.LockAsync(_managerId, Ct)).Error.ShouldBe(IdentityErrors.Forbidden);
        (await service.UnlockAsync(_managerId, Ct)).Error.ShouldBe(IdentityErrors.Forbidden);
        (await ServiceFor(new ScopedUserContext([])).ListAsync(null, new PageRequest(), Ct)).Error.ShouldBe(IdentityErrors.Forbidden);
        _audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task List_returns_the_directory_page()
    {
        _directory.Add("agent@demo.local", Roles.Agent);

        var page = (await AsManager().ListAsync("  agent ", new PageRequest(1, 10), Ct)).Value;

        page.TotalCount.ShouldBe(1);
        page.Items.ShouldHaveSingleItem().Email.ShouldBe("agent@demo.local");
    }

    [Fact]
    public async Task Create_user_assigns_the_role_sends_an_invitation_and_audits()
    {
        var result = await AsManager().CreateUserAsync(new CreateUserRequest(" agent2@demo.local ", " Agent Deux ", Roles.Agent), Ct);

        result.IsSuccess.ShouldBeTrue();
        _directory.Created.ShouldHaveSingleItem().ShouldBe(new NewUser("agent2@demo.local", "Agent Deux", Roles.Agent));
        _invitations.Invited.ShouldBe([result.Value]);
        var audit = _audit.Events.ShouldHaveSingleItem();
        audit.Action.ShouldBe(AuditActions.UserCreated);
        audit.EntityType.ShouldBe("User");
        audit.EntityId.ShouldBe(result.Value.ToString());
        audit.Details.ShouldBe("agent2@demo.local (Agent)");
        _unitOfWork.Executions.ShouldBe(1);
    }

    [Theory]
    [InlineData("not-an-email", "Name", Roles.Agent, "identity.email_invalid")]
    [InlineData("John <john@demo.local>", "Name", Roles.Agent, "identity.email_invalid")]
    [InlineData("", "Name", Roles.Agent, "identity.email_invalid")]
    [InlineData("new@demo.local", " ", Roles.Agent, "identity.display_name_invalid")]
    [InlineData("new@demo.local", "Name", "Owner", "identity.role_unknown")]
    [InlineData("GERANT@demo.local", "Name", Roles.Agent, "identity.email_taken")]
    public async Task Create_user_validates_its_input(string email, string displayName, string role, string expectedError)
    {
        var result = await AsManager().CreateUserAsync(new CreateUserRequest(email, displayName, role), Ct);

        result.Error.Code.ShouldBe(expectedError);
        _directory.Created.ShouldBeEmpty();
        _invitations.Invited.ShouldBeEmpty();
        _audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Display_name_is_limited()
    {
        var result = await AsManager().CreateUserAsync(
            new CreateUserRequest("new@demo.local", new string('a', UserAdministrationService.DisplayNameMaxLength + 1), Roles.Agent),
            Ct);

        result.Error.ShouldBe(IdentityErrors.DisplayNameInvalid);
    }

    [Fact]
    public async Task Lock_and_unlock_are_audited()
    {
        var agent = _directory.Add("agent@demo.local", Roles.Agent);
        var service = AsManager();

        (await service.LockAsync(agent.Id, Ct)).IsSuccess.ShouldBeTrue();
        (await service.UnlockAsync(agent.Id, Ct)).IsSuccess.ShouldBeTrue();

        _directory.LockChanges.ShouldBe([(agent.Id, true), (agent.Id, false)]);
        _audit.Events.Select(e => e.Action).ShouldBe([AuditActions.UserLocked, AuditActions.UserUnlocked]);
        _audit.Events.ShouldAllBe(e => e.EntityId == agent.Id.ToString() && e.Details == "agent@demo.local");
    }

    [Fact]
    public async Task Locking_an_already_locked_user_changes_nothing()
    {
        var agent = _directory.Add("agent@demo.local", Roles.Agent, locked: true);

        (await AsManager().LockAsync(agent.Id, Ct)).IsSuccess.ShouldBeTrue();

        _directory.LockChanges.ShouldBeEmpty();
        _audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_manager_cannot_lock_their_own_account()
    {
        (await AsManager().LockAsync(_managerId, Ct)).Error.ShouldBe(IdentityErrors.CannotLockSelf);
        _directory.LockChanges.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_last_active_manager_cannot_be_locked()
    {
        var otherManager = _directory.Add("gerant2@demo.local", Roles.Manager);
        var service = AsManager();
        _directory.Users[0] = _directory.Users[0] with { IsLockedOut = true };

        (await service.LockAsync(otherManager.Id, Ct)).Error.ShouldBe(IdentityErrors.LastManager);

        _directory.Users[0] = _directory.Users[0] with { IsLockedOut = false };
        (await service.LockAsync(otherManager.Id, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_users_are_reported()
    {
        (await AsManager().LockAsync(Guid.CreateVersion7(), Ct)).Error.ShouldBe(IdentityErrors.UserNotFound);
    }

    [Fact]
    public async Task A_manager_grants_and_revokes_extra_permissions_audited_and_refreshed()
    {
        var agent = _directory.Add("agent@demo.local", Roles.Agent);
        var service = AsManager();

        (await service.SetExtraPermissionsAsync(agent.Id, [Permissions.PricingManage, Permissions.ReportsRead], Ct)).IsSuccess.ShouldBeTrue();
        (await service.SetExtraPermissionsAsync(agent.Id, [Permissions.PricingManage], Ct)).IsSuccess.ShouldBeTrue();

        _directory.PermissionChanges.ShouldBe(
        [
            (agent.Id, Permissions.PricingManage, true),
            (agent.Id, Permissions.ReportsRead, true),
            (agent.Id, Permissions.ReportsRead, false),
        ]);
        _audit.Events.Select(e => (e.Action, e.Details)).ShouldBe(
        [
            (AuditActions.PermissionGranted, "pricing.manage (agent@demo.local)"),
            (AuditActions.PermissionGranted, "reports.read (agent@demo.local)"),
            (AuditActions.PermissionRevoked, "reports.read (agent@demo.local)"),
        ]);
        _audit.Events.ShouldAllBe(e => e.EntityType == "User" && e.EntityId == agent.Id.ToString());
        _sessions.Refreshed.ShouldBe([agent.Id, agent.Id]);
    }

    [Fact]
    public async Task Permissions_given_by_the_role_are_not_stored_and_an_unchanged_set_does_nothing()
    {
        var agent = _directory.Add("agent@demo.local", Roles.Agent, locked: false, Permissions.PricingManage);

        (await AsManager().SetExtraPermissionsAsync(agent.Id, [Permissions.PricingManage, Permissions.BookingsManage], Ct)).IsSuccess.ShouldBeTrue();

        _directory.PermissionChanges.ShouldBeEmpty();
        _audit.Events.ShouldBeEmpty();
        _sessions.Refreshed.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_permissions_of_a_user_separate_role_and_extra_ones()
    {
        var agent = _directory.Add("agent@demo.local", Roles.Agent, locked: false, Permissions.PricingManage);

        var permissions = (await AsManager().GetPermissionsAsync(agent.Id, Ct)).Value;

        permissions.Roles.ShouldBe([Roles.Agent]);
        permissions.RolePermissions.ShouldBe(RolePresets.PermissionsOf(Roles.Agent), ignoreOrder: true);
        permissions.ExtraPermissions.ShouldBe([Permissions.PricingManage]);
        permissions.EffectivePermissions.ShouldContain(Permissions.PricingRead);
        permissions.EffectivePermissions.ShouldContain(Permissions.BookingsManage);
        (await AsManager().GetPermissionsAsync(Guid.CreateVersion7(), Ct)).Error.ShouldBe(IdentityErrors.UserNotFound);
    }

    [Fact]
    public async Task Extra_permissions_are_checked()
    {
        var agent = _directory.Add("agent@demo.local", Roles.Agent);

        (await AsAccountant().SetExtraPermissionsAsync(agent.Id, [Permissions.PricingManage], Ct)).Error.ShouldBe(IdentityErrors.Forbidden);
        (await AsAccountant().GetPermissionsAsync(agent.Id, Ct)).Error.ShouldBe(IdentityErrors.Forbidden);
        (await AsManager().SetExtraPermissionsAsync(agent.Id, ["pricing.everything"], Ct)).Error.ShouldBe(IdentityErrors.PermissionUnknown);
        (await AsManager().SetExtraPermissionsAsync(_managerId, [Permissions.PricingManage], Ct)).Error.ShouldBe(IdentityErrors.CannotChangeOwnPermissions);
        (await AsManager().SetExtraPermissionsAsync(Guid.CreateVersion7(), [Permissions.PricingManage], Ct)).Error.ShouldBe(IdentityErrors.UserNotFound);

        _directory.PermissionChanges.ShouldBeEmpty();
        _sessions.Refreshed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Identity_queries_return_display_names()
    {
        var agent = _directory.Add("agent@demo.local", Roles.Agent);
        var queries = new IdentityQueries(_directory);

        (await queries.GetDisplayNamesAsync([agent.Id, Guid.CreateVersion7()], Ct)).ShouldBe(new Dictionary<Guid, string> { [agent.Id] = "agent@demo.local" });
        (await queries.GetDisplayNamesAsync([], Ct)).ShouldBeEmpty();
    }
}
