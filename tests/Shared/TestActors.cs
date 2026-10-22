using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;

namespace CarRental.Testing;

/// <summary>
/// The actors of the tests (ADR-0067): a member of an agency's staff works for that agency only, the support works for
/// every agency (or for the one they entered), a visitor of the public site for the agency of the site.
/// </summary>
public static class TestActors
{
    /// <summary>A member of <paramref name="agencyId"/> with the presets of <paramref name="role"/>.</summary>
    public static UserContextSnapshot Staff(
        Guid agencyId,
        string role,
        string? userName = null,
        Guid? userId = null,
        string? ipAddress = "203.0.113.20",
        string? correlationId = null) =>
        UserContextSnapshot.ForUser(
            userId ?? Guid.CreateVersion7(),
            userName ?? $"{role.ToLowerInvariant()}@tests.local",
            RolePresets.PermissionsOf(role),
            ipAddress,
            correlationId ?? Guid.NewGuid().ToString("N")) with
        {
            AgencyId = agencyId,
        };

    /// <summary>A member of <paramref name="agencyId"/> holding exactly <paramref name="permissions"/>.</summary>
    public static UserContextSnapshot WithPermissions(Guid agencyId, IEnumerable<string> permissions, string? userName = null, Guid? userId = null) =>
        UserContextSnapshot.ForUser(
            userId ?? Guid.CreateVersion7(),
            userName ?? "staff@tests.local",
            permissions,
            "203.0.113.20",
            Guid.NewGuid().ToString("N")) with
        {
            AgencyId = agencyId,
        };

    /// <summary>The support: across agencies, or in <paramref name="agencyId"/> when they entered it.</summary>
    public static UserContextSnapshot Support(
        Guid? agencyId = null,
        string? userName = "support@tests.local",
        Guid? userId = null,
        string? ipAddress = "203.0.113.30",
        string? correlationId = null) =>
        UserContextSnapshot.ForUser(
            userId ?? Guid.CreateVersion7(),
            userName,
            RolePresets.PermissionsOf(Roles.Support),
            ipAddress,
            correlationId ?? Guid.NewGuid().ToString("N")) with
        {
            AgencyId = agencyId,
            AllAgencies = true,
        };

    /// <summary>A visitor of the public site of <paramref name="agencyId"/>.</summary>
    public static UserContextSnapshot Visitor(Guid agencyId, string? ipAddress = "198.51.100.40") =>
        UserContextSnapshot.Anonymous(ipAddress, Guid.NewGuid().ToString("N")) with { AgencyId = agencyId };

    /// <summary>The system working for <paramref name="agencyId"/> only.</summary>
    public static UserContextSnapshot SystemFor(Guid agencyId, string name = "system:tests") =>
        UserContextSnapshot.System(name, agencyId: agencyId);
}
