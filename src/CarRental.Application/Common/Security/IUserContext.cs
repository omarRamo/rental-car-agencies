using CarRental.Domain.Common;

namespace CarRental.Application.Common.Security;

/// <summary>
/// Who is executing the current operation (HTTP request, Blazor circuit operation or background job), from where,
/// and with which permissions. Used for authorization checks in services and for traceability (audit log).
/// </summary>
public interface IUserContext
{
    Guid? UserId { get; }

    string? UserName { get; }

    bool IsAuthenticated { get; }

    /// <summary><see langword="true"/> for trusted platform code (background jobs, startup seeding).</summary>
    bool IsSystem { get; }

    string? IpAddress { get; }

    string? CorrelationId { get; }

    /// <summary>True when the user holds <paramref name="permission"/> (always true for the system actor).</summary>
    bool HasPermission(string permission);

    AuditActor ToAuditActor();
}
