using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Common.Security;
using CarRental.Application.Common.Tenancy;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Referential.Seeding;

/// <summary>
/// Runs the <see cref="IAgencyInitializer"/>s of every existing agency at each start (all environments, idempotent): an
/// agency registered before a module gained an initializer receives what it needs. Each agency in its own scope, as
/// <c>system:seed</c> working for that agency only (ADR-0067).
/// </summary>
internal sealed class AgencyProvisioningSeeder(IAppDbContext db, IServiceScopeFactory scopeFactory) : IDataSeeder
{
    public SeedStage Stage => SeedStage.Reference;

    public int Order => 250;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var agencies = await db.Set<Agency>().AsNoTracking().OrderBy(a => a.Name).Select(a => a.Id).ToListAsync(cancellationToken);
        foreach (var agencyId in agencies)
        {
            await ProvisionAsync(scopeFactory, agencyId, cancellationToken);
        }
    }

    /// <summary>Provisions one agency in a scope of its own, as <c>system:seed</c> working for that agency.</summary>
    internal static async Task ProvisionAsync(IServiceScopeFactory scopeFactory, Guid agencyId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:seed", agencyId: agencyId));
        await scope.ServiceProvider.GetRequiredService<AgencyProvisioning>().ProvisionAsync(agencyId, cancellationToken);
        await scope.ServiceProvider.GetRequiredService<IAppDbContext>().SaveChangesAsync(cancellationToken);
    }
}
