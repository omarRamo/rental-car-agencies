using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Availability;

/// <summary>Outcome of an availability check of one occupancy window.</summary>
public sealed record AvailabilityCheck
{
    private AvailabilityCheck(Period occupancy, int minimumRemaining, Period? firstConflict, AvailabilityConflictReason conflictReason)
    {
        Occupancy = occupancy;
        MinimumRemaining = minimumRemaining;
        FirstConflict = firstConflict;
        ConflictReason = conflictReason;
    }

    /// <summary>The checked window, preparation buffer included.</summary>
    public Period Occupancy { get; }

    public bool IsAvailable => FirstConflict is null;

    /// <summary>
    /// The lowest <c>capacity − occupancy</c> over the window, before the request is added: how many more requests of
    /// this window would still fit. At least 1 when available; 0 or less otherwise (negative when the category is
    /// already over capacity, e.g. a vehicle went to maintenance after bookings were taken).
    /// </summary>
    public int MinimumRemaining { get; }

    /// <summary>The first maximal sub-period of the window without capacity left; <see langword="null"/> when available.</summary>
    public Period? FirstConflict { get; }

    /// <summary>Why the first conflict has no capacity left; <see cref="AvailabilityConflictReason.None"/> when available.</summary>
    public AvailabilityConflictReason ConflictReason { get; }

    /// <summary>Success, or <see cref="AvailabilityErrors.NotAvailable"/>.</summary>
    public Result ToResult() => IsAvailable ? Result.Success() : AvailabilityErrors.NotAvailable;

    internal static AvailabilityCheck Available(Period occupancy, int minimumRemaining) =>
        new(occupancy, minimumRemaining, null, AvailabilityConflictReason.None);

    internal static AvailabilityCheck Unavailable(Period occupancy, int minimumRemaining, Period firstConflict, AvailabilityConflictReason reason) =>
        new(occupancy, minimumRemaining, firstConflict, reason);
}

/// <summary>Why a sub-period has no capacity left, evaluated at its start.</summary>
public enum AvailabilityConflictReason
{
    None = 0,

    /// <summary>Every vehicle in service is already taken (or none is in service): no channel could sell it.</summary>
    Capacity = 1,

    /// <summary>Vehicles are left but reserved for the other channels.</summary>
    Quota = 2,

    /// <summary>The requesting channel is in stop-sell.</summary>
    StopSale = 3,
}

/// <summary>A piece of the remaining-capacity profile of a category (planning, search).</summary>
public sealed record CapacitySegment(Period Period, int Capacity, int Occupancy)
{
    /// <summary><c>Capacity − Occupancy</c>: bookings that still fit during the segment (negative when over capacity).</summary>
    public int Remaining => Capacity - Occupancy;
}
