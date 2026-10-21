using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings;

/// <summary>
/// Cancellation policy of an agency (SPEC §6, docs/conception §1): free until D-<see cref="FreeUntilDaysBefore"/>, then
/// <see cref="FeePercent"/> of the total TTC of the current quote. Days are Tunis calendar days: a pick-up on the 10th
/// with D-3 is free when cancelled on the 7th (any time) and charged from the 8th.
/// </summary>
/// <remarks>
/// Only a firm booking (<see cref="BookingStatus.Confirmed"/>) is charged: a request, an option or a validated booking
/// awaiting its payment commits the customer to nothing yet. The refund or credit note comes with the payments
/// (phases 2 and 3); the fee is recorded on the file and in its timeline.
/// </remarks>
public sealed record CancellationPolicy
{
    public CancellationPolicy(int freeUntilDaysBefore, decimal feePercent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(freeUntilDaysBefore);
        if (feePercent is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(feePercent), feePercent, "The fee is a ratio between 0 and 1.");
        }

        FreeUntilDaysBefore = freeUntilDaysBefore;
        FeePercent = feePercent;
    }

    public int FreeUntilDaysBefore { get; }

    /// <summary>Ratio of the total TTC (0.20 for 20 %).</summary>
    public decimal FeePercent { get; }

    /// <summary>Last Tunis day of free cancellation for a pick-up at <paramref name="pickupAt"/>.</summary>
    public DateOnly FreeUntil(DateTimeOffset pickupAt) => TunisTime.DateOf(pickupAt).AddDays(-FreeUntilDaysBefore);

    /// <summary>The fee of a cancellation at <paramref name="cancelledAt"/> of a file in <paramref name="status"/>.</summary>
    public Money FeeFor(BookingStatus status, Money totalInclVat, DateTimeOffset pickupAt, DateTimeOffset cancelledAt)
    {
        if (status != BookingStatus.Confirmed || !totalInclVat.IsPositive || TunisTime.DateOf(cancelledAt) <= FreeUntil(pickupAt))
        {
            return Money.Zero;
        }

        return totalInclVat * FeePercent;
    }
}
