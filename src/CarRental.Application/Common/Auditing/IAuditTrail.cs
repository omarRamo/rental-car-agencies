namespace CarRental.Application.Common.Auditing;

/// <summary>
/// Writes explicit events to the append-only audit log (logins, lockouts, user administration, sensitive reads).
/// Entity changes are audited automatically on save; use this port for events that do not change an entity.
/// </summary>
public interface IAuditTrail
{
    /// <summary>Records the event now (it joins the ambient transaction, if any). Never put secrets in the event.</summary>
    Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}

/// <summary>An explicit audit event. The actor, IP address and correlation id come from the current user context.</summary>
/// <param name="Action">Stable dotted action code, e.g. <c>identity.login_succeeded</c>.</param>
public sealed record AuditEvent(string Action)
{
    /// <summary>Type of the subject, e.g. <c>User</c>.</summary>
    public string? EntityType { get; init; }

    /// <summary>Key of the subject.</summary>
    public string? EntityId { get; init; }

    /// <summary>Readable context (no secret, no password, no token).</summary>
    public string? Details { get; init; }

    /// <summary>
    /// Overrides the user of the context, e.g. the user who has just signed in (the request principal is still anonymous).
    /// </summary>
    public Guid? ActingUserId { get; init; }

    public string? ActingUserName { get; init; }

    /// <summary>
    /// The agency the event concerns (e.g. the agency of the user who has just signed in); the agency of the operation
    /// when <see langword="null"/>, the platform itself when the operation works for none (ADR-0067).
    /// </summary>
    public Guid? AgencyId { get; init; }

    /// <summary>
    /// The event concerns the platform itself whatever agency the operation works for, e.g. the account of the support,
    /// which belongs to no agency even while the support works inside one (ADR-0067): <see cref="AgencyId"/> is ignored.
    /// </summary>
    public bool ConcernsPlatform { get; init; }

    /// <summary>
    /// This event about the account of a user of <paramref name="userAgencyId"/>: recorded for that agency, or for the
    /// platform when the account belongs to none (the support).
    /// </summary>
    public AuditEvent ForAccountOf(Guid? userAgencyId) => this with { AgencyId = userAgencyId, ConcernsPlatform = userAgencyId is null };
}
