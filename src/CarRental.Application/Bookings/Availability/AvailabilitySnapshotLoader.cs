using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Availability;

/// <summary>
/// Reads the inputs of the availability engine from their owners, always fresh: capacity from the Fleet contract,
/// occupancy from the bookings (<see cref="IOccupancyReader"/>), restrictions from the channel policy, the
/// preparation buffer of the category's agency from the settings, and "now" from <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>Scoped: it uses the scope's <c>DbContext</c> through its dependencies, hence the caller's transaction.</remarks>
internal sealed class AvailabilitySnapshotLoader(
    IFleetQueries fleet,
    IOccupancyReader occupancyReader,
    IChannelAvailabilityPolicy channelPolicy,
    ISettingsReader settings,
    TimeProvider timeProvider)
{
    /// <summary>The category and the occupancy basis (now, buffer of its agency), or <see langword="null"/> when the category does not exist.</summary>
    public async Task<CategoryContext?> LoadContextAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var category = await fleet.GetCategoryAsync(categoryId, cancellationToken);
        if (category is null)
        {
            return null;
        }

        var buffer = await settings.GetAsync(WellKnownSettings.PreparationBuffer, category.AgencyId, cancellationToken);
        return new CategoryContext(category, new OccupancyBasis(timeProvider.GetUtcNow(), buffer));
    }

    /// <summary>The vehicles of the category with their rentable windows over <paramref name="coverage"/>.</summary>
    public async Task<IReadOnlyList<RentableVehicle>> LoadVehiclesAsync(Guid categoryId, Period coverage, CancellationToken cancellationToken)
    {
        var availability = await fleet.GetAvailabilityAsync(categoryId, coverage, cancellationToken);
        return [.. availability.Select(vehicle => new RentableVehicle(vehicle.VehicleId, vehicle.PlateNumber, vehicle.Windows))];
    }

    public Task<IReadOnlyList<OccupancyInterval>> LoadOccupancyAsync(
        CategoryContext context,
        Period coverage,
        Guid? excludedBookingId,
        CancellationToken cancellationToken) =>
        occupancyReader.GetOccupancyAsync(context.Category.Id, coverage, excludedBookingId, context.Basis, cancellationToken);

    /// <summary>Everything needed to answer availability over <paramref name="coverage"/>, for the given channels.</summary>
    public async Task<CategorySnapshot> LoadSnapshotAsync(
        CategoryContext context,
        Period coverage,
        Guid? excludedBookingId,
        IReadOnlyCollection<SalesChannel> channels,
        CancellationToken cancellationToken)
    {
        var vehicles = await LoadVehiclesAsync(context.Category.Id, coverage, cancellationToken);
        var occupancy = await LoadOccupancyAsync(context, coverage, excludedBookingId, cancellationToken);
        var restrictions = new Dictionary<SalesChannel, ChannelRestrictions>();
        foreach (var channel in channels)
        {
            var info = await channelPolicy.GetRestrictionsAsync(context.Category.Id, channel, coverage, cancellationToken);
            restrictions[channel] = ToDomain(info);
        }

        return new CategorySnapshot(context, coverage, vehicles, occupancy, restrictions);
    }

    private static ChannelRestrictions ToDomain(ChannelRestrictionsInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.ReservedForOtherChannels.Count == 0 && info.StopSales.Count == 0)
        {
            return ChannelRestrictions.None;
        }

        return new ChannelRestrictions(
            info.ReservedForOtherChannels.Select(reserved => new ReservedCapacity(reserved.Period, reserved.Vehicles)),
            info.StopSales);
    }
}

/// <summary>A category and the occupancy basis its availability is computed with.</summary>
internal sealed record CategoryContext(VehicleCategoryInfo Category, OccupancyBasis Basis);
