using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Tenancy;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Referential;

/// <summary>
/// The office of a new agency as its first pick-up and return location (code <see cref="OfficeCode"/>, the agency's name
/// and address), so that it can take bookings at once; its manager adds the airports and delivery points. Nothing when
/// the agency already has a location.
/// </summary>
internal sealed class ReferentialAgencyInitializer(IAppDbContext db) : IAgencyInitializer
{
    public const string OfficeCode = "AGENCE";

    public int Order => 100;

    public async Task InitializeAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        if (await db.Set<Location>().AnyAsync(l => l.AgencyId == agencyId, cancellationToken))
        {
            return;
        }

        var agency = await db.Set<Agency>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == agencyId, cancellationToken);
        if (agency is null)
        {
            return;
        }

        var office = Location.Create(agencyId, OfficeCode, agency.Name, LocationKind.AgencyOffice, agency.Address);
        if (office.IsSuccess)
        {
            db.Set<Location>().Add(office.Value);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
