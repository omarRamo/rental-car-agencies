using System.Diagnostics.CodeAnalysis;

namespace CarRental.Application.Identity.Contracts;

/// <summary>
/// Authorization policy names for permissions: <c>Permission:{name}</c>, e.g. <c>Permission:audit.read</c>.
/// The web host resolves them dynamically (custom policy provider): authenticated, signed in with MFA, and holding the permission.
/// </summary>
public static class PermissionPolicy
{
    public const string Prefix = "Permission:";

    public static string For(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return Prefix + permission;
    }

    public static bool TryGetPermission(string? policyName, [NotNullWhen(true)] out string? permission)
    {
        if (policyName is not null
            && policyName.StartsWith(Prefix, StringComparison.Ordinal)
            && policyName.Length > Prefix.Length)
        {
            permission = policyName[Prefix.Length..];
            return true;
        }

        permission = null;
        return false;
    }
}
