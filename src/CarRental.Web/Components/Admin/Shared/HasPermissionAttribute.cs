using CarRental.Application.Identity.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace CarRental.Web.Components.Admin.Shared;

/// <summary>
/// Requires a permission (policy <c>Permission:{name}</c>: authenticated, MFA sign-in, permission claim).
/// Applied to every back office page; architecture tests check it and its consistency with the navigation.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    public HasPermissionAttribute(string permission)
        : base(PermissionPolicy.For(permission))
    {
        Permission = permission;
    }

    public string Permission { get; }
}
