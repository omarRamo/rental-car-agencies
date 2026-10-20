using CarRental.Infrastructure.Identity;
using CarRental.Infrastructure.Persistence.Auditing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Identity;

// ASP.NET Core Identity tables, renamed and moved to the "identity" schema. Secrets are masked in the audit log;
// technical noise (normalized copies, concurrency stamps, failed counters, last sign-in) is left out of it.

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.ToTable("Users", Schemas.Identity);
        builder.HasAuditName("User");
        builder.Property(u => u.DisplayName).HasMaxLength(ApplicationUser.DisplayNameMaxLength).IsRequired();
        builder.Property(u => u.PasswordHash).IsAuditSensitive();
        builder.Property(u => u.SecurityStamp).IsAuditSensitive();
        builder.Property(u => u.ConcurrencyStamp).IsAuditIgnored();
        builder.Property(u => u.NormalizedEmail).IsAuditIgnored();
        builder.Property(u => u.NormalizedUserName).IsAuditIgnored();
        builder.Property(u => u.AccessFailedCount).IsAuditIgnored();
        builder.Property(u => u.LastSignInAtUtc).IsAuditIgnored();
    }
}

internal sealed class ApplicationRoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        builder.ToTable("Roles", Schemas.Identity);
        builder.HasAuditName("Role");
        builder.Property(r => r.ConcurrencyStamp).IsAuditIgnored();
        builder.Property(r => r.NormalizedName).IsAuditIgnored();
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<IdentityUserRole<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserRole<Guid>> builder)
    {
        builder.ToTable("UserRoles", Schemas.Identity);
        builder.HasAuditName("UserRole");
    }
}

internal sealed class UserClaimConfiguration : IEntityTypeConfiguration<IdentityUserClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserClaim<Guid>> builder)
    {
        builder.ToTable("UserClaims", Schemas.Identity);
        builder.HasAuditName("UserClaim");
    }
}

internal sealed class RoleClaimConfiguration : IEntityTypeConfiguration<IdentityRoleClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityRoleClaim<Guid>> builder)
    {
        builder.ToTable("RoleClaims", Schemas.Identity);
        builder.HasAuditName("RoleClaim");
    }
}

internal sealed class UserLoginConfiguration : IEntityTypeConfiguration<IdentityUserLogin<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserLogin<Guid>> builder)
    {
        builder.ToTable("UserLogins", Schemas.Identity);
        builder.HasAuditName("UserLogin");
        builder.Property(l => l.ProviderKey).IsAuditSensitive();
    }
}

internal sealed class UserTokenConfiguration : IEntityTypeConfiguration<IdentityUserToken<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> builder)
    {
        builder.ToTable("UserTokens", Schemas.Identity);
        builder.HasAuditName("UserToken");

        // Authenticator keys and recovery codes: never written to the audit log.
        builder.Property(t => t.Value).IsAuditSensitive();
    }
}
