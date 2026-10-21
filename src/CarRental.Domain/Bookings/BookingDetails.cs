using System.ComponentModel.DataAnnotations.Schema;
using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings;

/// <summary>What a rental file is created with (every channel): agency, channel, category, places and planned instants,
/// parties, extras, promotion code, guaranteed vehicle, channel reference, attribution and internal notes.</summary>
public sealed record BookingDetails(
    Guid AgencyId,
    SalesChannel Channel,
    Guid CategoryId,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    BookingParties Parties,
    IReadOnlyList<ExtraChoice>? Extras = null,
    string? PromoCode = null,
    Guid? GuaranteedVehicleId = null,
    string? ChannelReference = null,
    BookingAttribution? Attribution = null,
    string? Notes = null);

/// <summary>
/// The new state of the amendable fields of a rental file (start from <see cref="Booking.CurrentChanges"/> and change
/// what is amended). <see cref="ReleaseVehicle"/> unassigns the vehicle in the same command (dates it cannot carry).
/// </summary>
[NotMapped]
public sealed record BookingChanges(
    Guid CategoryId,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    BookingParties Parties,
    IReadOnlyList<ExtraChoice> Extras,
    string? PromoCode,
    Guid? GuaranteedVehicleId,
    string? Notes,
    bool ReleaseVehicle = false)
{
    /// <summary>The planned rental of the changes.</summary>
    public Result<Period> Rental => Period.Create(PickupAt, ReturnAt) is { IsSuccess: true } period ? period : BookingErrors.PeriodInvalid;
}
