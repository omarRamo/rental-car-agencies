namespace CarRental.Application.Common.Availability;

/// <summary>
/// In-process signal that the capacity (fleet) or the occupancy (bookings) of a vehicle category changed, so that the
/// availability cache drops its snapshot (PROMPT §8: cache invalidated by events). Call it after a successful commit.
/// </summary>
/// <remarks>
/// The authoritative check always runs against the database under the availability lock; the cache only serves fast
/// reads (public search, bot, channels). Until the Bookings module registers the cache (with
/// <c>services.Replace(...)</c>), the default implementation does nothing.
/// </remarks>
public interface IAvailabilityChangeNotifier
{
    /// <summary>The capacity or occupancy of <paramref name="categoryId"/> changed.</summary>
    void CategoryChanged(Guid categoryId);

    /// <summary>Something changed that may affect every category (e.g. a setting such as the preparation buffer).</summary>
    void AllChanged();
}

/// <summary>Default notifier used until the availability cache exists.</summary>
internal sealed class NullAvailabilityChangeNotifier : IAvailabilityChangeNotifier
{
    public void CategoryChanged(Guid categoryId)
    {
    }

    public void AllChanged()
    {
    }
}
