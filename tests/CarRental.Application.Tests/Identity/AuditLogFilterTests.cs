using CarRental.Application.Identity.Audit;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;

namespace CarRental.Application.Tests.Identity;

/// <summary>The audit log filters, evaluated in memory (the SQL translation is covered by the integration tests).</summary>
public sealed class AuditLogFilterTests
{
    private static readonly DateTimeOffset Day = new(2027, 6, 28, 0, 0, 0, TimeSpan.Zero);

    private static readonly AuditLog[] Logs =
    [
        AuditLog.Record(Day.AddHours(8), new AuditActor(null, "gerant@demo.local", null, "c1"), AuditActions.LoginSucceeded, "User", "1"),
        AuditLog.Record(Day.AddHours(9), new AuditActor(null, "gerant@demo.local", null, "c2"), AuditActions.EntityUpdated, "Agency", "42"),
        AuditLog.Record(Day.AddHours(10), new AuditActor(null, "agent@demo.local", null, "c3"), AuditActions.LoginFailed, "User", "2"),
        AuditLog.Record(Day.AddDays(1), AuditActor.System("system:outbox"), AuditActions.EntityCreated, "Setting", "7"),
    ];

    private static List<string> Actions(AuditLogFilter filter) =>
        AuditLogQueries.Apply(Logs.AsQueryable(), filter).Select(l => l.Action).ToList();

    [Fact]
    public void Empty_filter_keeps_everything()
    {
        Actions(new AuditLogFilter()).Count.ShouldBe(4);
    }

    [Fact]
    public void Period_bounds_are_inclusive_then_exclusive()
    {
        Actions(new AuditLogFilter { FromUtc = Day.AddHours(9), ToUtc = Day.AddHours(10) }).ShouldBe([AuditActions.EntityUpdated]);
    }

    [Fact]
    public void User_filter_matches_part_of_the_name()
    {
        Actions(new AuditLogFilter { User = " agent " }).ShouldBe([AuditActions.LoginFailed]);
        Actions(new AuditLogFilter { User = "system" }).ShouldBe([AuditActions.EntityCreated]);
    }

    [Fact]
    public void Action_filter_is_exact_or_a_prefix()
    {
        Actions(new AuditLogFilter { Action = AuditActions.LoginFailed }).ShouldBe([AuditActions.LoginFailed]);
        Actions(new AuditLogFilter { Action = "identity.*" }).ShouldBe([AuditActions.LoginSucceeded, AuditActions.LoginFailed]);
        Actions(new AuditLogFilter { Action = "identity" }).ShouldBeEmpty();
    }

    [Fact]
    public void Agency_filter_keeps_the_entries_of_one_agency_or_of_the_platform()
    {
        var (tunis, sousse) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        AuditLog[] logs =
        [
            AuditLog.Record(Day, new AuditActor(null, "gerant@demo.local", null, "a1"), AuditActions.LoginSucceeded, agencyId: tunis),
            AuditLog.Record(Day, new AuditActor(null, "gerant.sousse@demo.local", null, "a2"), AuditActions.LoginSucceeded, agencyId: sousse),
            AuditLog.Record(Day, AuditActor.System("system:outbox"), AuditActions.EntityCreated),
        ];
        List<string?> Users(AuditLogFilter filter) => [.. AuditLogQueries.Apply(logs.AsQueryable(), filter).Select(l => l.UserName)];

        Users(new AuditLogFilter { AgencyId = sousse }).ShouldBe(["gerant.sousse@demo.local"]);
        Users(new AuditLogFilter { PlatformOnly = true }).ShouldBe(["system:outbox"]);
        Users(new AuditLogFilter { PlatformOnly = true, AgencyId = tunis }).ShouldBe(["system:outbox"], "the platform choice wins");
        Users(new AuditLogFilter()).Count.ShouldBe(3);
    }

    [Fact]
    public void Entity_and_correlation_filters_are_exact()
    {
        Actions(new AuditLogFilter { EntityType = "User", EntityId = "2" }).ShouldBe([AuditActions.LoginFailed]);
        Actions(new AuditLogFilter { CorrelationId = "c2" }).ShouldBe([AuditActions.EntityUpdated]);
        Actions(new AuditLogFilter { EntityType = "  " }).Count.ShouldBe(4);
    }
}
