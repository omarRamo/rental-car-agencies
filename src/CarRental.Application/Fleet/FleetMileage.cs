using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;

namespace CarRental.Application.Fleet;

/// <summary>
/// <see cref="IFleetMileage"/>: the reading of a hand-over recorded through the fleet write pipeline (never lower, an
/// equal reading is a no-op, the number of the rental file in the vehicle history, audited).
/// </summary>
internal sealed class FleetMileage(FleetWriter writer, IUserContext userContext) : IFleetMileage
{
    public async Task<Result> RecordRentalMileageAsync(
        Guid vehicleId,
        int mileage,
        Guid bookingId,
        string bookingNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookingNumber);
        if (bookingId == Guid.Empty)
        {
            throw new ArgumentException("The reading belongs to a rental file.", nameof(bookingId));
        }

        if (!userContext.HasPermission(Permissions.HandoversManage)
            && !userContext.HasPermission(Permissions.FleetMaintenance)
            && !userContext.HasPermission(Permissions.FleetManage))
        {
            return FleetErrors.Forbidden;
        }

        return await writer.ChangeVehicleAsync(
            vehicleId,
            (vehicle, stamp) => vehicle.RecordMileage(mileage, stamp, reason: bookingNumber),
            cancellationToken);
    }
}
