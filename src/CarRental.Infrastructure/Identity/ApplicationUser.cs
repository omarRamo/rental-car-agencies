using Microsoft.AspNetCore.Identity;

namespace CarRental.Infrastructure.Identity;

/// <summary>
/// An internal user: a member of one agency's staff (manager, agent, accountant, preparer) or the support of the platform
/// (ADR-0067). Customers have no account.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public const int DisplayNameMaxLength = 100;

    public ApplicationUser()
    {
        Id = Guid.CreateVersion7();
        SecurityStamp = Guid.NewGuid().ToString("N");
    }

    /// <summary>Name shown in the back office and in the audit log.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? LastSignInAtUtc { get; set; }

    /// <summary>
    /// The agency of a staff member: the only one they see and work for. <see langword="null"/> for the support, who
    /// works for every agency.
    /// </summary>
    public Guid? AgencyId { get; set; }

    /// <summary>
    /// The agency the support entered from the platform pages (its back office opens as for its manager), until they
    /// leave it; always <see langword="null"/> for a staff member.
    /// </summary>
    public Guid? SelectedAgencyId { get; set; }

    /// <summary>The agency the sessions of this user work for: their own agency, or the one the support entered.</summary>
    public Guid? CurrentAgencyId => AgencyId ?? SelectedAgencyId;
}

/// <summary>A role: a preset of permissions stored as role claims.</summary>
public sealed class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole()
    {
        Id = Guid.CreateVersion7();
    }

    public ApplicationRole(string roleName)
        : base(roleName)
    {
        Id = Guid.CreateVersion7();
    }
}
