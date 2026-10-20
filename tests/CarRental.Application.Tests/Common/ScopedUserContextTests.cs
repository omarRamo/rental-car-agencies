using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;

namespace CarRental.Application.Tests.Common;

public sealed class ScopedUserContextTests
{
    [Fact]
    public void Without_explicit_or_ambient_context_the_actor_is_anonymous()
    {
        var context = new ScopedUserContext([]);

        context.IsAuthenticated.ShouldBeFalse();
        context.IsSystem.ShouldBeFalse();
        context.UserId.ShouldBeNull();
        context.HasPermission(Permissions.AuditRead).ShouldBeFalse();
    }

    [Fact]
    public void Ambient_provider_is_used_when_not_initialized()
    {
        var userId = Guid.CreateVersion7();
        var ambient = UserContextSnapshot.ForUser(userId, "agent@demo.local", [Permissions.BookingsRead], "198.51.100.4", "trace");
        var context = new ScopedUserContext([new StubAmbientProvider(null), new StubAmbientProvider(ambient)]);

        context.UserId.ShouldBe(userId);
        context.UserName.ShouldBe("agent@demo.local");
        context.IpAddress.ShouldBe("198.51.100.4");
        context.CorrelationId.ShouldBe("trace");
        context.HasPermission(Permissions.BookingsRead).ShouldBeTrue();
        context.HasPermission(Permissions.BookingsManage).ShouldBeFalse();
    }

    [Fact]
    public void Explicit_initialization_wins_and_can_happen_only_once()
    {
        var context = new ScopedUserContext([new StubAmbientProvider(UserContextSnapshot.Anonymous("10.0.0.1"))]);
        var system = UserContextSnapshot.System("system:outbox", "c");

        context.Initialize(system);
        context.Initialize(system);

        context.IsSystem.ShouldBeTrue();
        context.UserName.ShouldBe("system:outbox");
        Should.Throw<InvalidOperationException>(() => context.Initialize(UserContextSnapshot.Anonymous()));
    }

    [Fact]
    public void System_actor_holds_every_permission()
    {
        var context = new ScopedUserContext([]);
        context.Initialize(UserContextSnapshot.System("system:seed"));

        Permissions.All.ShouldAllBe(p => context.HasPermission(p));
    }

    [Fact]
    public void Audit_actor_reflects_the_snapshot()
    {
        var userId = Guid.CreateVersion7();
        var context = new ScopedUserContext([]);
        context.Initialize(UserContextSnapshot.ForUser(userId, "gerant@demo.local", [], "203.0.113.1", "corr"));

        var actor = context.ToAuditActor();

        actor.UserId.ShouldBe(userId);
        actor.UserName.ShouldBe("gerant@demo.local");
        actor.IpAddress.ShouldBe("203.0.113.1");
        actor.CorrelationId.ShouldBe("corr");
    }

    [Fact]
    public void Guard_clauses()
    {
        var context = new ScopedUserContext([]);

        Should.Throw<ArgumentNullException>(() => context.Initialize(null!));
        Should.Throw<ArgumentException>(() => context.HasPermission(" "));
        Should.Throw<ArgumentException>(() => UserContextSnapshot.System(""));
    }

    private sealed class StubAmbientProvider(UserContextSnapshot? snapshot) : IAmbientUserContextProvider
    {
        public UserContextSnapshot? GetCurrent() => snapshot;
    }
}
