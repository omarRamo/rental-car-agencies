using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Reporting;

internal sealed class FakeFleetReporting : IFleetReportingQueries
{
    public List<FleetVehicleHistoryInfo> Vehicles { get; } = [];

    public List<FleetVehicleCostInfo> Costs { get; } = [];

    public Task<IReadOnlyList<FleetVehicleHistoryInfo>> GetVehicleDaysAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FleetVehicleHistoryInfo>>(
        [
            .. Vehicles.Select(v => v with { Days = [.. v.Days.Where(d => d.Day >= from && d.Day <= to)] }).Where(v => v.Days.Count > 0),
        ]);

    public Task<IReadOnlyList<FleetVehicleCostInfo>> GetVehicleCostsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FleetVehicleCostInfo>>([.. Costs.Where(c => c.To >= from && c.From <= to)]);
}

internal sealed class FakeBookingsReporting : IBookingsReportingQueries
{
    public List<BookingFactsInfo> Bookings { get; } = [];

    public Task<IReadOnlyList<BookingFactsInfo>> GetBookingsOverlappingAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var start = TunisTime.StartOfDay(from);
        var end = TunisTime.StartOfDay(to.AddDays(1));
        return Task.FromResult<IReadOnlyList<BookingFactsInfo>>([.. Bookings.Where(b => b.PickupAt < end && b.ReturnAt > start)]);
    }

    public Task<IReadOnlyList<BookingFactsInfo>> GetRequestsSubmittedAsync(Guid agencyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BookingFactsInfo>>([.. Bookings.Where(b => b.SubmittedAtUtc >= fromUtc && b.SubmittedAtUtc < toUtc)]);
}

internal sealed class FakeCostSource(string name) : IVehicleCostsSource
{
    public List<VehicleCostInfo> Costs { get; } = [];

    public string Name { get; } = name;

    public Task<IReadOnlyList<VehicleCostInfo>> GetCostsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleCostInfo>>([.. Costs]);
}

internal sealed class FakeCommissions : IChannelCommissionSource
{
    public Dictionary<Guid, Money> Commissions { get; } = [];

    public List<IReadOnlyCollection<Guid>> Asked { get; } = [];

    public Task<IReadOnlyDictionary<Guid, Money>> GetCommissionsAsync(Guid agencyId, IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken = default)
    {
        Asked.Add(bookingIds);
        return Task.FromResult<IReadOnlyDictionary<Guid, Money>>(Commissions.Where(c => bookingIds.Contains(c.Key)).ToDictionary());
    }
}

internal sealed class FakeAgencies(params AgencyInfo[] agencies) : IReferentialQueries
{
    public Task<IReadOnlyList<AgencyInfo>> GetAgenciesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgencyInfo>>(agencies);

    public Task<IReadOnlyList<LocationInfo>> GetLocationsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocationInfo>>([]);

    public Task<LocationInfo?> FindLocationByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult<LocationInfo?>(null);
}
