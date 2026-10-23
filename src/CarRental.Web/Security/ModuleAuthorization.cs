using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using Microsoft.AspNetCore.Authorization;

namespace CarRental.Web.Security;

/// <summary>
/// Authorization policy names for the optional modules of the agencies (ADR-0067): <c>Module:{flags}</c>, e.g.
/// <c>Module:Cash</c>. Met when the agency of the session is active and has the module on; never for the support outside
/// any agency.
/// </summary>
public static class ModulePolicy
{
    public const string Prefix = "Module:";

    public static string For(AgencyModules module)
    {
        if (module == AgencyModules.None || !AgencyModuleCatalog.AreDefined(module))
        {
            throw new ArgumentOutOfRangeException(nameof(module), module, "A module policy names defined modules.");
        }

        return Prefix + module.ToString();
    }

    public static bool TryGetModule(string? policyName, [NotNullWhen(true)] out AgencyModules? module)
    {
        module = null;
        if (policyName is null || !policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (Enum.TryParse<AgencyModules>(policyName[Prefix.Length..], ignoreCase: false, out var parsed)
            && parsed != AgencyModules.None
            && AgencyModuleCatalog.AreDefined(parsed)
            && !int.TryParse(policyName[Prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            module = parsed;
            return true;
        }

        return false;
    }
}

/// <summary>The agency of the session has the module on (see <see cref="ModulePolicy"/>).</summary>
public sealed class ModuleRequirement(AgencyModules module) : IAuthorizationRequirement
{
    public AgencyModules Module { get; } = module;
}

internal sealed class ModuleAuthorizationHandler(IAgencyDirectory agencies) : AuthorizationHandler<ModuleRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ModuleRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        if (context.User.GetAgencyId() is { } agencyId && await agencies.HasModuleAsync(agencyId, requirement.Module))
        {
            context.Succeed(requirement);
        }
    }
}
