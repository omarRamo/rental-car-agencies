using System.Security.Claims;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Security;

namespace CarRental.Web.Components.Admin.Shared;

/// <summary>
/// The agency the back office of a session works for (ADR-0067): the agency of a staff member, or the agency the support
/// entered; none for the support on the platform pages. Read from the session claim and the agency directory.
/// </summary>
public sealed class CurrentAgency(IAgencyDirectory agencies)
{
    /// <summary>The agency of <paramref name="user"/>'s session, <see langword="null"/> when none (or when it no longer exists).</summary>
    public async Task<AgencySummary?> GetAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.GetAgencyId() is { } agencyId ? await agencies.FindAsync(agencyId, cancellationToken) : null;
    }

    /// <summary>True when the session's agency has every module of <paramref name="module"/> on (always true for <see langword="null"/>).</summary>
    public static bool Offers(AgencySummary? agency, AgencyModules? module) =>
        module is not { } required || (agency is { IsActive: true } && agency.HasModule(required));
}
