using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;

namespace CarRental.Application.Bookings.Handover;

/// <summary>The parts of the hand-over screens shared by the departure and the return.</summary>
internal static class HandoverContextLoader
{
    public static async Task<HandoverBookingView> BookingAsync(
        Booking booking,
        QuoteStore quotes,
        IFleetQueries fleet,
        IReferentialQueries referential,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var quote = await quotes.CurrentAsync(booking, cancellationToken);
        var category = await fleet.GetCategoryAsync(booking.CategoryId, cancellationToken);
        var locations = await referential.GetLocationsAsync(booking.AgencyId, cancellationToken);
        return new HandoverBookingView(
            booking.Id,
            booking.Number,
            booking.AgencyId,
            booking.Status,
            booking.Channel,
            booking.ChannelReference,
            booking.CategoryId,
            category?.AcrissCode ?? string.Empty,
            category?.Name ?? string.Empty,
            booking.PickupAt,
            booking.ReturnAt,
            booking.ActualPickupAt,
            booking.ActualReturnAt,
            locations.FirstOrDefault(l => l.Id == booking.PickupLocationId)?.Name ?? string.Empty,
            locations.FirstOrDefault(l => l.Id == booking.ReturnLocationId)?.Name ?? string.Empty,
            booking.TotalInclVat,
            booking.Deposit,
            quote?.IncludesFranchiseWaiver ?? false,
            quote?.Days ?? 0,
            booking.IsLateAt(now));
    }

    /// <summary>The vehicle assigned to the file, and whether its mandatory documents cover the rental (or are waived).</summary>
    public static async Task<HandoverVehicleView?> VehicleAsync(Booking booking, IFleetQueries fleet, CancellationToken cancellationToken)
    {
        if (booking.AssignedVehicleId is not { } vehicleId || await fleet.GetVehicleAsync(vehicleId, cancellationToken) is not { } vehicle)
        {
            return null;
        }

        return new HandoverVehicleView(
            vehicle.Id,
            vehicle.PlateNumber,
            vehicle.Make,
            vehicle.Model,
            vehicle.Year,
            vehicle.Mileage,
            vehicle.Fuel,
            vehicle.CompliantUntil,
            vehicle.CompliantUntil is { } until && booking.ReturnAt <= until,
            booking.IsComplianceWaived);
    }

    public static HandoverAbilities Abilities(IUserContext user) => new(
        Counter: user.HasPermission(Permissions.BookingsManage),
        ManagePayments: user.HasPermission(Permissions.PaymentsManage),
        DeferPayment: user.HasPermission(Permissions.HandoversPaymentDefer),
        AssignVehicle: user.HasPermission(Permissions.BookingsManage),
        WaiveCompliance: user.HasPermission(Permissions.BookingsComplianceWaive),
        EditCharges: user.HasPermission(Permissions.PaymentsManage),
        Close: user.HasPermission(Permissions.BookingsManage));
}
