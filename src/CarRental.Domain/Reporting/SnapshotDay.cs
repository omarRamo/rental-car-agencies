using CarRental.Domain.Common;

namespace CarRental.Domain.Reporting;

/// <summary>
/// A computed day of the snapshots of an agency (ADR-0045): proof that the day was computed (even without any vehicle in
/// the fleet), when, for how many vehicles, and the rental revenue of that day that no vehicle-day could carry — a counted
/// booking without vehicle, or a rented day when its vehicle was not in the fleet — reported apart instead of lost.
/// Replaced with the rows of the day when it is computed again.
/// </summary>
public sealed class SnapshotDay : Entity
{
    private SnapshotDay()
    {
    }

    private SnapshotDay(Guid id, Guid agencyId, DateOnly day)
        : base(id)
    {
        AgencyId = agencyId;
        Day = day;
    }

    public Guid AgencyId { get; private set; }

    public DateOnly Day { get; private set; }

    /// <summary>Vehicles in the fleet that day (snapshot rows).</summary>
    public int VehicleCount { get; private set; }

    /// <summary>Rental revenue of the day carried by no vehicle-day, excluding VAT.</summary>
    public Money UnattributedRevenue { get; private set; }

    /// <summary>Rented days of counted bookings carried by no vehicle-day.</summary>
    public int UnattributedRentedDays { get; private set; }

    public DateTimeOffset ComputedAtUtc { get; private set; }

    internal static SnapshotDay Create(Guid agencyId, DateOnly day, int vehicleCount, Money unattributedRevenue, int unattributedRentedDays, DateTimeOffset computedAt)
    {
        if (agencyId == Guid.Empty)
        {
            throw new ArgumentException("A computed day belongs to an agency.", nameof(agencyId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(vehicleCount);
        ArgumentOutOfRangeException.ThrowIfNegative(unattributedRentedDays);
        return new SnapshotDay(NewId(), agencyId, day)
        {
            VehicleCount = vehicleCount,
            UnattributedRevenue = unattributedRevenue,
            UnattributedRentedDays = unattributedRentedDays,
            ComputedAtUtc = computedAt.ToUniversalTime(),
        };
    }
}
