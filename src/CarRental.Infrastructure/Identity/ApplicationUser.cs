using Microsoft.AspNetCore.Identity;

namespace CarRental.Infrastructure.Identity;

/// <summary>An internal user (manager, agent, accountant, preparer). Customers have no account.</summary>
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
