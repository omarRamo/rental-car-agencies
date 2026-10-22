namespace CarRental.Application.Bookings.Contracts;

/// <summary>
/// Read API of the Bookings module for the pre-accounting (package 3B): the readable numbers (<c>LOC-AAAA-NNNNNN</c>) of
/// many rental files in one call — the lettering key that ties an invoice to the payments of its file. No permission
/// check (the caller checks its own).
/// </summary>
/// <remarks>A separate interface from <see cref="IBookingsQueries"/>, so that its implementers are not affected.</remarks>
public interface IBookingNumbers
{
    /// <summary>The number of each of <paramref name="bookingIds"/> that exists (others are absent).</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNumbersAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken = default);
}
