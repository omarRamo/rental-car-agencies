using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;

namespace CarRental.Application.Tests.Pricing;

/// <summary>In-memory <see cref="IRatePlanStore"/>: plans are kept as objects (the unit of work is inline).</summary>
internal sealed class InMemoryRatePlanStore : IRatePlanStore
{
    public List<RatePlan> Plans { get; } = [];

    public Task<RatePlan?> FindAsync(Guid planId, CancellationToken cancellationToken) =>
        Task.FromResult(Plans.Find(p => p.Id == planId));

    public Task<RatePlan?> FindPublishedAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken) =>
        Task.FromResult(Plans.Find(p => p.AgencyId == agencyId && p.Channel == channel && p.Status == RatePlanStatus.Published));

    public Task<bool> DraftExistsAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken) =>
        Task.FromResult(Plans.Exists(p => p.AgencyId == agencyId && p.Channel == channel && p.Status == RatePlanStatus.Draft));

    public Task<Guid?> FindAgencyIdAsync(Guid planId, CancellationToken cancellationToken) =>
        Task.FromResult(Plans.Find(p => p.Id == planId)?.AgencyId);

    public Task<int> GetLastVersionAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken) =>
        Task.FromResult(Plans.Where(p => p.AgencyId == agencyId && p.Channel == channel).Max(p => p.Version) ?? 0);

    public Task<IReadOnlyList<RatePlan>> ListAsync(Guid? agencyId, bool includeArchived, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RatePlan>>(
            [.. Plans.Where(p => (agencyId is null || p.AgencyId == agencyId) && (includeArchived || p.Status != RatePlanStatus.Archived))]);

    public Task<IReadOnlyList<RatePlan>> ListVersionsAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RatePlan>>(
            [.. Plans.Where(p => p.AgencyId == agencyId && p.Channel == channel).OrderBy(p => p.Version is null ? 0 : 1).ThenByDescending(p => p.Version)]);

    public Task<bool> AnyForAgencyAsync(Guid agencyId, CancellationToken cancellationToken) =>
        Task.FromResult(Plans.Exists(p => p.AgencyId == agencyId));

    public void Add(RatePlan plan) => Plans.Add(plan);
}

/// <summary>Fleet contract with in-memory categories and vehicles.</summary>
internal sealed class FakeFleetQueries : IFleetQueries
{
    public List<VehicleCategoryInfo> Categories { get; } = [];

    public Task<IReadOnlyList<VehicleDeadlineInfo>> GetDeadlinesAsync(Guid vehicleId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleDeadlineInfo>>([]);

    public List<VehicleInfo> Vehicles { get; } = [];

    public VehicleCategoryInfo AddCategory(Guid id, Guid agencyId, string code, decimal deposit, decimal reducedDeposit, bool active = true)
    {
        var category = new VehicleCategoryInfo(
            id,
            agencyId,
            code,
            code,
            "Peugeot 208",
            5,
            5,
            1,
            1,
            TransmissionInfo.Manual,
            FuelTypeInfo.Petrol,
            AirConditioning: true,
            Money.Tnd(deposit),
            Money.Tnd(reducedDeposit),
            PhotoUrl: null,
            DisplayOrder: Categories.Count,
            IsActive: active);
        Categories.Add(category);
        return category;
    }

    public VehicleInfo AddVehicle(Guid categoryId, decimal? supplement, VehicleStatusInfo status = VehicleStatusInfo.Active)
    {
        var category = Categories.Single(c => c.Id == categoryId);
        var vehicle = new VehicleInfo(
            Guid.CreateVersion7(),
            category.AgencyId,
            categoryId,
            "123 TU 4567",
            "Peugeot",
            "208",
            2025,
            TransmissionInfo.Manual,
            FuelTypeInfo.Petrol,
            5,
            12_000,
            status,
            supplement is { } amount ? Money.Tnd(amount) : null,
            CompliantUntil: null);
        Vehicles.Add(vehicle);
        return vehicle;
    }

    public Task<IReadOnlyList<VehicleCategoryInfo>> GetCategoriesAsync(Guid? agencyId = null, bool includeInactive = false, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleCategoryInfo>>(
            [.. Categories.Where(c => (agencyId is null || c.AgencyId == agencyId) && (includeInactive || c.IsActive))]);

    public Task<VehicleCategoryInfo?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Categories.Find(c => c.Id == categoryId));

    public Task<VehicleCategoryInfo?> FindCategoryByCodeAsync(Guid agencyId, string acrissCode, CancellationToken cancellationToken = default) =>
        Task.FromResult(Categories.Find(c => c.AgencyId == agencyId && c.AcrissCode == acrissCode && c.IsActive));

    public Task<VehicleInfo?> GetVehicleAsync(Guid vehicleId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Vehicles.Find(v => v.Id == vehicleId));

    public Task<IReadOnlyList<VehicleInfo>> GetVehiclesAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleInfo>>([.. Vehicles.Where(v => v.CategoryId == categoryId)]);

    public Task<IReadOnlyList<VehicleAvailabilityInfo>> GetAvailabilityAsync(Guid categoryId, Period range, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleAvailabilityInfo>>([]);
}

/// <summary>Referential contract with in-memory agencies and locations.</summary>
internal sealed class FakeReferentialQueries : IReferentialQueries
{
    public List<AgencyInfo> Agencies { get; } = [];

    public List<LocationInfo> Locations { get; } = [];

    public Task<IReadOnlyList<AgencyInfo>> GetAgenciesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgencyInfo>>([.. Agencies]);

    public Task<IReadOnlyList<LocationInfo>> GetLocationsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocationInfo>>([.. Locations.Where(l => agencyId is null || l.AgencyId == agencyId)]);

    public Task<LocationInfo?> FindLocationByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(Locations.Find(l => l.Code == code));
}

/// <summary>Settings reader returning the stored values of the test, else the definition defaults.</summary>
internal sealed class FakeSettingsReader : ISettingsReader
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    public List<(string Key, Guid? AgencyId)> Reads { get; } = [];

    public void Set<T>(SettingDefinition<T> definition, T value)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(definition);
        _values[definition.Key] = value;
    }

    public Task<T> GetAsync<T>(SettingDefinition<T> definition, Guid? agencyId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Reads.Add((definition.Key, agencyId));
        return Task.FromResult(_values.TryGetValue(definition.Key, out var value) ? (T)value : definition.Default);
    }
}
