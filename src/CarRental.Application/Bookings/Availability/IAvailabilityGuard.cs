using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Availability;

/// <summary>
/// Authoritative availability check of the writers (ADR-0006, ADR-0025): always on fresh data — never the cache — and
/// under the exclusive availability lock (<c>sp_getapplock</c> on <c>availability:{categoryId}</c>) owned by the
/// current transaction.
/// </summary>
/// <remarks>
/// <para>Write protocol, for every write that consumes capacity (create, <c>Submit</c>, <c>Amend</c>,
/// <c>ConfirmOption</c>, <c>ProvideInformation</c>, extension…): inside <c>IUnitOfWork.ExecuteAsync</c>, call
/// <see cref="EnsureAvailableAsync"/> first (lock, then re-check), write in the same transaction, and after the commit
/// call <c>IAvailabilityChangeNotifier.CategoryChanged</c> for each category touched. The second of two concurrent
/// requests for the last vehicle waits for the lock, then receives <see cref="AvailabilityErrors.NotAvailable"/>.</para>
/// <para>Writes that only release capacity or record facts (cancellation, refusal, expiry, return) need no check.
/// The lock is re-entrant within a transaction, so taking it again here is harmless.</para>
/// </remarks>
public interface IAvailabilityGuard
{
    /// <summary>
    /// Acquires the availability locks of <paramref name="categoryIds"/>, once each, in ascending id order so that two
    /// writers locking the same categories never deadlock. Requires an open unit of work.
    /// </summary>
    /// <returns>Success, or the lock error (<c>availability.lock_timeout</c>, <c>availability.lock_deadlock</c>…).</returns>
    /// <exception cref="InvalidOperationException">Called outside <c>IUnitOfWork.ExecuteAsync</c>.</exception>
    Task<Result> LockAsync(IEnumerable<Guid> categoryIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks the category of the request — and <paramref name="previousCategoryId"/> on a category change, both in
    /// ascending id order — then checks on fresh data that the booking fits, the booking itself left out.
    /// </summary>
    /// <returns>Success; <see cref="AvailabilityErrors.NotAvailable"/>; <see cref="AvailabilityErrors.CategoryNotFound"/>; or a lock error.</returns>
    /// <exception cref="InvalidOperationException">Called outside <c>IUnitOfWork.ExecuteAsync</c>.</exception>
    Task<Result> EnsureAvailableAsync(
        AvailabilityRequest request,
        Guid? previousCategoryId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks the category, then checks on fresh data that the vehicle can carry the booking: no overlap with its other
    /// bookings (buffer included), documents valid until the return unless waived, rentable over the occupancy.
    /// </summary>
    /// <returns>Success; <c>availability.vehicle_overlap</c>, <c>vehicle_not_compliant</c>, <c>vehicle_unavailable</c>,
    /// <c>vehicle_not_in_category</c>, <c>category_not_found</c>; or a lock error.</returns>
    /// <exception cref="InvalidOperationException">Called outside <c>IUnitOfWork.ExecuteAsync</c>.</exception>
    Task<Result> EnsureVehicleAssignableAsync(VehicleAssignmentRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// A booking about to consume capacity of <c>CategoryId</c>, sold on <c>Channel</c>, described as it will be after the
/// write (<c>Booking</c>: kind, planned rental, actual pick-up of a rental in progress being extended).
/// </summary>
public sealed record AvailabilityRequest(Guid CategoryId, SalesChannel Channel, BookingOccupancy Booking)
{
    /// <summary>
    /// Checks the physical capacity only, ignoring the quotas and stop-sales of the channel: for an agent entering a
    /// booking already sold by a channel (e.g. Booking.com while its stop-sell was not yet reported on the extranet).
    /// </summary>
    public bool IgnoreChannelRestrictions { get; init; }
}

/// <summary>Assignment of <c>VehicleId</c> to a booking of <c>CategoryId</c>.</summary>
public sealed record VehicleAssignmentRequest(Guid CategoryId, BookingOccupancy Booking, Guid VehicleId)
{
    /// <summary>The manager waived the document check for this booking (traced by the caller); never waives an overlap.</summary>
    public bool ComplianceWaived { get; init; }
}
