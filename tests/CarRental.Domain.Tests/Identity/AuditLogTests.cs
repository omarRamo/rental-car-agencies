using CarRental.Domain.Identity;

namespace CarRental.Domain.Tests.Identity;

public sealed class AuditLogTests
{
    private static readonly DateTimeOffset At = new(2027, 6, 28, 10, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void Record_copies_the_actor_and_normalizes_the_timestamp()
    {
        var userId = Guid.CreateVersion7();
        var actor = new AuditActor(userId, "gerant@demo.local", "203.0.113.7", "trace-1");

        var log = AuditLog.Record(At, actor, AuditActions.EntityUpdated, "Agency", "42", "{\"Name\":\"A\"}", "{\"Name\":\"B\"}");

        log.OccurredAtUtc.ShouldBe(At);
        log.OccurredAtUtc.Offset.ShouldBe(TimeSpan.Zero);
        log.UserId.ShouldBe(userId);
        log.UserName.ShouldBe("gerant@demo.local");
        log.IpAddress.ShouldBe("203.0.113.7");
        log.CorrelationId.ShouldBe("trace-1");
        log.Action.ShouldBe(AuditActions.EntityUpdated);
        log.EntityType.ShouldBe("Agency");
        log.EntityId.ShouldBe("42");
        log.Before.ShouldBe("{\"Name\":\"A\"}");
        log.After.ShouldBe("{\"Name\":\"B\"}");
    }

    [Fact]
    public void Long_values_are_truncated_to_column_sizes()
    {
        var actor = new AuditActor(null, new string('u', 400), new string('i', 100), new string('c', 100));

        var log = AuditLog.Record(At, actor, AuditActions.LoginFailed, details: new string('d', 3000));

        log.UserName!.Length.ShouldBe(AuditActor.UserNameMaxLength);
        log.IpAddress!.Length.ShouldBe(AuditActor.IpAddressMaxLength);
        log.CorrelationId!.Length.ShouldBe(AuditActor.CorrelationIdMaxLength);
        log.Details!.Length.ShouldBe(AuditLog.DetailsMaxLength);
    }

    [Fact]
    public void System_actor_has_no_user()
    {
        var actor = AuditActor.System("system:outbox", "corr");

        actor.UserId.ShouldBeNull();
        actor.UserName.ShouldBe("system:outbox");
        actor.CorrelationId.ShouldBe("corr");
    }

    [Fact]
    public void Action_and_actor_are_required()
    {
        Should.Throw<ArgumentException>(() => AuditLog.Record(At, AuditActor.System("s"), " "));
        Should.Throw<ArgumentNullException>(() => AuditLog.Record(At, null!, AuditActions.Logout));
    }

    [Fact]
    public void Document_access_is_recorded_with_its_actor()
    {
        var documentId = Guid.CreateVersion7();
        var actor = new AuditActor(Guid.CreateVersion7(), "agent@demo.local", "198.51.100.1", "c");

        var log = DocumentAccessLog.Record(At, actor, documentId, "passport", "view", "Vérification au comptoir");

        log.DocumentId.ShouldBe(documentId);
        log.DocumentKind.ShouldBe("passport");
        log.Access.ShouldBe("view");
        log.Reason.ShouldBe("Vérification au comptoir");
        log.UserName.ShouldBe("agent@demo.local");
        log.OccurredAtUtc.Offset.ShouldBe(TimeSpan.Zero);
        Should.Throw<ArgumentException>(() => DocumentAccessLog.Record(At, actor, Guid.Empty, "passport", "view"));
        Should.Throw<ArgumentException>(() => DocumentAccessLog.Record(At, actor, documentId, " ", "view"));
    }
}
