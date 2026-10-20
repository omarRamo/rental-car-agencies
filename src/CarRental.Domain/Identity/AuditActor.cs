namespace CarRental.Domain.Identity;

/// <summary>Who performed an action, from where, within which correlated request or job.</summary>
/// <param name="UserId">Authenticated user, or <see langword="null"/> for anonymous visitors and system jobs.</param>
/// <param name="UserName">User name or system actor name (e.g. <c>system:outbox</c>).</param>
/// <param name="IpAddress">Client IP address as seen by the application (after trusted proxies).</param>
/// <param name="CorrelationId">Trace or correlation identifier linking the row to logs and traces.</param>
public sealed record AuditActor(Guid? UserId, string? UserName, string? IpAddress, string? CorrelationId)
{
    public const int UserNameMaxLength = 256;
    public const int IpAddressMaxLength = 64;
    public const int CorrelationIdMaxLength = 64;

    /// <summary>The platform itself (background jobs, startup seeding).</summary>
    public static AuditActor System(string name, string? correlationId = null) => new(null, name, null, correlationId);
}
