using CarRental.Domain.Common;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Authorization;

namespace CarRental.Web.Components.Admin.Shared;

/// <summary>
/// The page belongs to an optional module of the agencies (policy <c>Module:{module}</c>, ADR-0067): it opens only while
/// the module is on for the agency of the session. Put next to <see cref="HasPermissionAttribute"/>; architecture tests
/// check it against the module of its menu entry.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequiresModuleAttribute : AuthorizeAttribute
{
    public RequiresModuleAttribute(AgencyModules module)
        : base(ModulePolicy.For(module))
    {
        Module = module;
    }

    public AgencyModules Module { get; }
}
