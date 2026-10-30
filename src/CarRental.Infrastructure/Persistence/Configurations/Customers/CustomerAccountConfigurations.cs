using CarRental.Domain.Customers;
using CarRental.Domain.Customers.Accounts;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Auditing;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Customers;

/// <summary>
/// Customer accounts of the mobile application and of the storefronts (ADR-0070): the account (temporal; personal data
/// and credentials masked in the audit log), its refresh tokens and codes (hashes only, a journal of their own, left out
/// of the audit log, purged by a job) and the bookings made with it. The e-mail of an account is unique within its
/// agency; the hash of a refresh token is unique on the platform, like every random secret.
/// </summary>
internal sealed class CustomerAccountConfiguration : IEntityTypeConfiguration<CustomerAccount>
{
    public void Configure(EntityTypeBuilder<CustomerAccount> builder)
    {
        builder.ToTemporalTable("CustomerAccounts", Schemas.Customers);
        builder.HasKey(a => a.Id);
        builder.HasOne<Agency>().WithMany().HasForeignKey(a => a.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(a => a.Email).HasMaxLength(EmailAddresses.MaxLength).IsRequired().IsAuditSensitive();
        builder.Property(a => a.PasswordHash).HasMaxLength(CustomerAccount.PasswordHashMaxLength).IsUnicode(false).IsRequired().IsAuditIgnored();
        builder.Property(a => a.FirstName).HasMaxLength(CustomerAccount.NameMaxLength).IsRequired().IsAuditSensitive();
        builder.Property(a => a.LastName).HasMaxLength(CustomerAccount.NameMaxLength).IsRequired().IsAuditSensitive();
        builder.Property(a => a.Phone).HasMaxLength(PhoneNumbers.MaxLength).IsUnicode(false).IsAuditSensitive();
        builder.Property(a => a.Culture).HasMaxLength(CustomerAccount.CultureMaxLength).IsUnicode(false).IsRequired();
        builder.Property(a => a.SecurityStamp).HasMaxLength(CustomerAccount.SecurityStampLength).IsFixedLength().IsUnicode(false).IsRequired().IsAuditIgnored();
        builder.Property(a => a.LastModifiedAtUtc).IsAuditIgnored();
        builder.Property(a => a.LastSignInAtUtc).IsAuditIgnored();
        builder.Property(a => a.FailedSignInCount).IsAuditIgnored();
        builder.Property(a => a.LockedUntilUtc).IsAuditIgnored();
        builder.Ignore(a => a.IsEmailVerified);
        builder.Ignore(a => a.IsClosed);
        builder.Ignore(a => a.FullName);

        // One account per address and per agency (the e-mail of a closed account becomes a tombstone).
        builder.HasIndex(a => new { a.AgencyId, a.Email }).IsUnique();
        builder.HasIndex(a => new { a.AgencyId, a.PersonId });
    }
}

internal sealed class CustomerRefreshTokenConfiguration : IEntityTypeConfiguration<CustomerRefreshToken>
{
    public void Configure(EntityTypeBuilder<CustomerRefreshToken> builder)
    {
        builder.ToTable("CustomerRefreshTokens", Schemas.Customers);
        builder.ExcludeFromAudit();
        builder.HasKey(t => t.Id);
        builder.HasOne<Agency>().WithMany().HasForeignKey(t => t.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CustomerAccount>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(t => t.TokenHash).HasMaxLength(AccountSecrets.HashLength).IsFixedLength().IsRequired();
        builder.Property(t => t.Device).HasMaxLength(CustomerRefreshToken.DeviceMaxLength);
        builder.Property(t => t.IpAddress).HasMaxLength(CustomerRefreshToken.IpAddressMaxLength).IsUnicode(false);

        // Unique on the whole platform on purpose: the hash of a random secret token.
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => new { t.AccountId, t.Family });
        builder.HasIndex(t => t.ExpiresAtUtc);
    }
}

internal sealed class CustomerAccountCodeConfiguration : IEntityTypeConfiguration<CustomerAccountCode>
{
    public void Configure(EntityTypeBuilder<CustomerAccountCode> builder)
    {
        builder.ToTable("CustomerAccountCodes", Schemas.Customers);
        builder.ExcludeFromAudit();
        builder.HasKey(c => c.Id);
        builder.HasOne<Agency>().WithMany().HasForeignKey(c => c.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CustomerAccount>().WithMany().HasForeignKey(c => c.AccountId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(c => c.Purpose).HasConversion<int>();
        builder.Property(c => c.CodeHash).HasMaxLength(AccountSecrets.HashLength).IsFixedLength().IsRequired();
        builder.Property(c => c.CodeSalt).HasMaxLength(AccountSecrets.SaltLength).IsFixedLength().IsRequired();
        builder.HasIndex(c => new { c.AccountId, c.Purpose, c.CreatedAtUtc });
        builder.HasIndex(c => c.CreatedAtUtc);
    }
}

internal sealed class CustomerAccountBookingConfiguration : IEntityTypeConfiguration<CustomerAccountBooking>
{
    public void Configure(EntityTypeBuilder<CustomerAccountBooking> builder)
    {
        builder.ToTable("CustomerAccountBookings", Schemas.Customers);
        builder.HasKey(l => l.Id);
        builder.HasOne<Agency>().WithMany().HasForeignKey(l => l.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CustomerAccount>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(l => new { l.AccountId, l.BookingId }).IsUnique();
        builder.HasIndex(l => l.BookingId);
    }
}
