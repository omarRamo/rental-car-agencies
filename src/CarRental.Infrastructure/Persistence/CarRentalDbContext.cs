using CarRental.Application.Common.Persistence;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Identity;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context of the platform (ADR-0012): ASP.NET Core Identity tables plus every module, one SQL
/// schema per module. Saving runs the interceptors (soft delete, domain events to outbox, audit log).
/// </summary>
/// <remarks>
/// To add an entity: create <c>Persistence/Configurations/{Module}/{Entity}Configuration.cs</c>
/// (<see cref="IEntityTypeConfiguration{TEntity}"/>, table in the module schema, <c>ToTemporalTable</c> for key
/// aggregates), then add a migration. Conventions (ids, rowversion, soft delete filter, decimal(18,3), Money) are automatic.
/// </remarks>
public sealed class CarRentalDbContext(DbContextOptions<CarRentalDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options), IAppDbContext
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.OnModelCreating(builder);

        // Passkeys (Identity schema version 3) are not used: the second factor is TOTP (ADR-0014). The .NET 10 base
        // context still exposes a UserPasskeys set, so it is ignored explicitly (same model at runtime and design time).
        builder.Ignore<IdentityUserPasskey<Guid>>();
        builder.Ignore<IdentityPasskeyData>();

        builder.ApplyConfigurationsFromAssembly(typeof(CarRentalDbContext).Assembly);
        builder.ApplyCarRentalConventions();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Money everywhere: decimal(18,3), millimes. Rates and percentages override the precision explicitly.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 3);
        configurationBuilder.Properties<Money>().HaveConversion<MoneyConverter>().HavePrecision(18, 3);
        configurationBuilder.Properties<AcrissCode>().HaveConversion<AcrissCodeConverter>().HaveMaxLength(4).AreUnicode(false);
    }
}
