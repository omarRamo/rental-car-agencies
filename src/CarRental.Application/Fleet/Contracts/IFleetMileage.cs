using CarRental.Domain.Common;

namespace CarRental.Application.Fleet.Contracts;

/// <summary>
/// The only way for another module to update the mileage of a vehicle: the odometer reading taken at a hand-over of a
/// rental file (departure and return inspections, Bookings module). The mileage never decreases — a lower reading is
/// refused (<c>fleet.vehicle.mileage_decrease</c>), an equal one changes nothing —, the vehicle history keeps the number of
/// the file, and the change is audited like every write.
/// </summary>
/// <remarks>
/// It can run inside the unit of work of the caller (the return of a rental): it then joins its transaction, and a failed
/// result must fail the caller's unit of work too. The caller holds <c>handovers.manage</c> (or a fleet permission).
/// </remarks>
public interface IFleetMileage
{
    Task<Result> RecordRentalMileageAsync(
        Guid vehicleId,
        int mileage,
        Guid bookingId,
        string bookingNumber,
        CancellationToken cancellationToken = default);
}
