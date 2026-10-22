using CarRental.Application.Referential.Contracts;

namespace CarRental.Application.Bookings.PublicAccess;

/// <summary>
/// The address of an agency's public site (<c>Agency.Slug</c>), first segment of every link written for its customers
/// (<see cref="Contracts.PublicBookingPaths"/>, ADR-0067): read from the cached directory by the agency of the booking.
/// </summary>
internal static class PublicSiteAddress
{
    public static async Task<string> SlugAsync(this IAgencyDirectory agencies, Guid agencyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agencies);
        return (await agencies.FindAsync(agencyId, cancellationToken))?.Slug
               ?? throw new InvalidOperationException($"The agency {agencyId} does not exist: its links have no address.");
    }
}
