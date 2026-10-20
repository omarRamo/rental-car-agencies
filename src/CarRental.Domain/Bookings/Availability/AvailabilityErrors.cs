using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Availability;

/// <summary>
/// Stable error codes of the availability engine; the user-facing texts are the resources <c>Error.&lt;code&gt;</c>.
/// </summary>
public static class AvailabilityErrors
{
    /// <summary>
    /// The category has no vehicle left for the period on the requesting channel (capacity reached, quota or stop-sell).
    /// Of two concurrent requests for the last vehicle, the second one receives this error.
    /// </summary>
    public static readonly Error NotAvailable = Error.Conflict(
        "availability.not_available",
        "The category has no vehicle left for this period on this channel.");

    public static readonly Error VehicleOverlap = Error.Conflict(
        "availability.vehicle_overlap",
        "The vehicle already carries an overlapping booking (preparation buffer included).");

    public static readonly Error VehicleNotCompliant = Error.Conflict(
        "availability.vehicle_not_compliant",
        "A mandatory document of the vehicle (insurance, road tax, technical inspection) expires before the return.");

    public static readonly Error VehicleUnavailable = Error.Conflict(
        "availability.vehicle_unavailable",
        "The vehicle cannot be rented for the whole period (maintenance, out of service, sold or not yet in service).");

    public static readonly Error VehicleNotInCategory = Error.Validation(
        "availability.vehicle_not_in_category",
        "The vehicle does not belong to the category of the booking.");

    public static readonly Error NoVehicleInService = Error.Conflict(
        "availability.no_vehicle_in_service",
        "No vehicle of the category can be rented for the whole period.");

    public static readonly Error AllVehiclesOccupied = Error.Conflict(
        "availability.all_vehicles_occupied",
        "Every vehicle that can be rented for the period already carries an overlapping booking.");

    public static readonly Error CategoryNotFound = Error.NotFound(
        "availability.category_not_found",
        "The vehicle category does not exist.");
}
