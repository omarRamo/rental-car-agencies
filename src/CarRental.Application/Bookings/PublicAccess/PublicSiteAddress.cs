using CarRental.Application.Referential.Contracts;

namespace CarRental.Application.Bookings.PublicAccess;

/// <summary>
/// The address of an agency's public site as the links written for its customers start (<see cref="Contracts.PublicBookingPaths"/>,
/// ADR-0067, ADR-0069): the address of the site on the platform host (<c>Agency.Slug</c>), or nothing when the site is
/// served on the agency's own host (the e-mail composer then writes absolute links on that host). Read from the cached
/// directory by the agency of the booking.
/// </summary>
internal static class PublicSiteAddress
{
    /// <summary>
    /// The address of the site on the platform host (<c>Agency.Slug</c>), for the paths the platform itself handles
    /// whatever the host of the agency's site: the return addresses of the payment gateway (one merchant account, one
    /// return address, ADR-0067; the return then sends the browser to the agency's own host, ADR-0069).
    /// </summary>
    public static async Task<string> SlugAsync(this IAgencyDirectory agencies, Guid agencyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agencies);
        return (await agencies.FindAsync(agencyId, cancellationToken))?.Slug
               ?? throw new InvalidOperationException($"The agency {agencyId} does not exist: its links have no address.");
    }

    public static async Task<string> LinkSlugAsync(this IAgencyDirectory agencies, Guid agencyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agencies);
        var agency = await agencies.FindAsync(agencyId, cancellationToken)
                     ?? throw new InvalidOperationException($"The agency {agencyId} does not exist: its links have no address.");
        return agency.LinkSlug;
    }
}
