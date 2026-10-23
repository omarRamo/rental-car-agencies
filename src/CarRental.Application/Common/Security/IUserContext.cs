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

    /// <summary>The agency the operation works for, <see langword="null"/> when none (ADR-0067, <see cref="UserContextSnapshot.AgencyId"/>).</summary>
    Guid? AgencyId { get; }

    /// <summary>
    /// True when the operation may work for every agency (system actor or support, see <see cref="UserContextSnapshot.AllAgencies"/>),
    /// whether or not it currently works for one.
    /// </summary>
    bool AllAgencies { get; }

    /// <summary>
    /// True when the operation reads and writes the data of every agency (system actor or support outside any agency);
    /// otherwise it only sees the data of <see cref="AgencyId"/> (nothing without one).
    /// </summary>
    bool IsAcrossAgencies { get; }

    /// <summary>True when the user holds <paramref name="permission"/> (always true for the system actor).</summary>
    bool HasPermission(string permission);

    AuditActor ToAuditActor();
}
