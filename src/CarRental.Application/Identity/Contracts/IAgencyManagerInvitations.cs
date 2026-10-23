using CarRental.Domain.Common;

namespace CarRental.Application.Identity.Contracts;

/// <summary>
/// Creates the manager account of an agency from the platform pages (support only, ADR-0067) and sends its invitation
/// e-mail (link to choose a password). Joins the caller's unit of work: the agency and its first manager are registered
/// together or not at all.
/// </summary>
public interface IAgencyManagerInvitations
{
    /// <summary>The new manager's id; fails on an invalid or already used e-mail address, or outside the platform pages.</summary>
    Task<Result<Guid>> InviteManagerAsync(Guid agencyId, string email, string displayName, CancellationToken cancellationToken = default);
}
