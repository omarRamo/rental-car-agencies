using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;

namespace CarRental.Application.Identity.Users;

/// <summary>
/// The agency the support works in (ADR-0067): from the platform pages they enter an agency (active or suspended) and
/// work in its back office as its manager would, then leave it to come back to the platform. Each entry and exit is
/// recorded in the journal of the agency. Run across agencies (the web host refreshes the session afterwards).
/// </summary>
public sealed class SupportSessionService(
    IUserDirectory directory,
    IAgencyDirectory agencies,
    IAuditTrail auditTrail,
    IUserContext userContext)
{
    private const string UserEntityType = "User";

    public async Task<Result> EnterAgencyAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        var agency = await agencies.FindAsync(agencyId, cancellationToken);
        if (agency is null)
        {
            return IdentityErrors.AgencyNotFound;
        }

        var userId = userContext.UserId!.Value;
        var entered = await directory.SetSelectedAgencyAsync(userId, agency.Id, cancellationToken);
        if (entered.IsFailure)
        {
            return entered;
        }

        await auditTrail.RecordAsync(
            new AuditEvent(AuditActions.SupportEnteredAgency)
            {
                EntityType = UserEntityType,
                EntityId = userId.ToString(),
                Details = agency.Name,
                AgencyId = agency.Id,
            },
            cancellationToken);
        return Result.Success();
    }

    public async Task<Result> LeaveAgencyAsync(CancellationToken cancellationToken = default)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        var userId = userContext.UserId!.Value;
        var previous = await directory.FindSelectedAgencyAsync(userId, cancellationToken);
        if (previous is not { } agencyId)
        {
            return Result.Success();
        }

        var left = await directory.SetSelectedAgencyAsync(userId, agencyId: null, cancellationToken);
        if (left.IsFailure)
        {
            return left;
        }

        await auditTrail.RecordAsync(
            new AuditEvent(AuditActions.SupportLeftAgency)
            {
                EntityType = UserEntityType,
                EntityId = userId.ToString(),
                Details = (await agencies.FindAsync(agencyId, cancellationToken))?.Name,
                AgencyId = agencyId,
            },
            cancellationToken);
        return Result.Success();
    }

    /// <summary>A signed-in support user, working across agencies (platform scope); otherwise the error to return.</summary>
    private Error? Denied() =>
        userContext.UserId is null || !userContext.HasPermission(Permissions.PlatformAgencies) ? IdentityErrors.Forbidden
        : !userContext.IsAcrossAgencies ? IdentityErrors.PlatformScopeRequired
        : null;
}
