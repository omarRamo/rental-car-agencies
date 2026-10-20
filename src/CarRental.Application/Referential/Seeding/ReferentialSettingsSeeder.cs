using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Referential.Seeding;

/// <summary>Creates the platform-wide value of every well-known setting that is missing (all environments; never overwrites).</summary>
internal sealed class ReferentialSettingsSeeder(IAppDbContext db) : IDataSeeder
{
    public SeedStage Stage => SeedStage.Reference;

    public int Order => 200;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var existingKeys = await db.Set<Setting>()
            .Where(s => s.AgencyId == null)
            .Select(s => s.Key)
            .ToListAsync(cancellationToken);

        foreach (var definition in WellKnownSettings.All.Where(d => !existingKeys.Contains(d.Key)))
        {
            db.Set<Setting>().Add(Setting.Create(definition.Key, definition.DefaultValue).Value);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
