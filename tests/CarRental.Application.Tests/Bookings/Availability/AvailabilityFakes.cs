using System.Globalization;
using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Concurrency;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Bookings.Availability;

/// <summary>Ordered log of the calls made to the fakes, to check that the lock comes before any read.</summary>
internal sealed class CallJournal
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public void Add(string entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }
}

/// <summary>In-memory Fleet contract: categories, vehicles and windows clipped to the requested range.</summary>
internal sealed class FakeFleetQueries(CallJournal journal) : IFleetQueries
{
    private readonly Lock _gate = new();
    private readonly List<VehicleCategoryInfo> _categories = [];
    private readonly List<(VehicleInfo Vehicle, Period[] Windows)> _vehicles = [];
    private int _categoryReads;
    private int _availabilityReads;

    /// <summary>When set, reading a category waits for it (to hold a cache load in flight).</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Completed when a category read starts.</summary>
    public TaskCompletionSource CategoryReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, the next availability read throws it.</summary>
    public Exception? NextAvailabilityFailure { get; set; }

    public int CategoryReads => Volatile.Read(ref _categoryReads);

    public int AvailabilityReads => Volatile.Read(ref _availabilityReads);

    public VehicleCategoryInfo AddCategory(Guid agencyId, string acrissCode, int displayOrder = 0, bool isActive = true, Guid? id = null)
    {
        var category = new VehicleCategoryInfo(
            id ?? Guid.CreateVersion7(),
            agencyId,
            acrissCode,
            $"Category {acrissCode}",
            "Peugeot 208",
            5,
            4,
            1,
            1,
            TransmissionInfo.Manual,
            FuelTypeInfo.Petrol,
            AirConditioning: true,
            Money.Tnd(500m),
            Money.Tnd(200m),
            PhotoUrl: null,
            displayOrder,
            isActive);
        lock (_gate)
        {
            _categories.Add(category);
        }

        return category;
    }

    public VehicleInfo AddVehicle(
        VehicleCategoryInfo category,
        string plateNumber,
        IEnumerable<Period> windows,
        DateTimeOffset? compliantUntil = null,
        VehicleStatusInfo status = VehicleStatusInfo.Active)
    {
        var vehicle = new VehicleInfo(
            Guid.CreateVersion7(),
            category.AgencyId,
            category.Id,
            plateNumber,
            "Peugeot",
            "208",
            2025,
            TransmissionInfo.Manual,
            FuelTypeInfo.Petrol,
            5,
            12_000,
            status,
            GuaranteedModelDailySupplement: null,
            compliantUntil ?? DateTimeOffset.MaxValue);
        lock (_gate)
        {
            _vehicles.Add((vehicle, [.. windows]));
        }

        return vehicle;
    }

    public void SetWindows(Guid vehicleId, params Period[] windows)
    {
        lock (_gate)
        {
            var index = _vehicles.FindIndex(v => v.Vehicle.Id == vehicleId);
            _vehicles[index] = (_vehicles[index].Vehicle, windows);
        }
    }

    public Task<IReadOnlyList<VehicleCategoryInfo>> GetCategoriesAsync(Guid? agencyId = null, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        journal.Add("fleet.categories");
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<VehicleCategoryInfo>>(
            [
                .. _categories
                    .Where(c => (agencyId is null || c.AgencyId == agencyId) && (includeInactive || c.IsActive))
                    .OrderBy(c => c.DisplayOrder)
                    .ThenBy(c => c.AcrissCode, StringComparer.Ordinal),
            ]);
        }
    }

    public async Task<VehicleCategoryInfo?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        journal.Add("fleet.category");
        Interlocked.Increment(ref _categoryReads);
        CategoryReadStarted.TrySetResult();
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        lock (_gate)
        {
            return _categories.FirstOrDefault(c => c.Id == categoryId);
        }
    }

    public Task<VehicleCategoryInfo?> FindCategoryByCodeAsync(Guid agencyId, string acrissCode, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_categories.FirstOrDefault(c => c.AgencyId == agencyId && c.IsActive && c.AcrissCode == acrissCode));
        }
    }

    public Task<VehicleInfo?> GetVehicleAsync(Guid vehicleId, CancellationToken cancellationToken = default)
    {
        journal.Add("fleet.vehicle");
        lock (_gate)
        {
            return Task.FromResult(_vehicles.Select(v => v.Vehicle).FirstOrDefault(v => v.Id == vehicleId));
        }
    }

    public Task<IReadOnlyList<VehicleInfo>> GetVehiclesAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<VehicleInfo>>(
            [
                .. _vehicles.Select(v => v.Vehicle)
                    .Where(v => v.CategoryId == categoryId && v.Status != VehicleStatusInfo.Sold)
                    .OrderBy(v => v.PlateNumber, StringComparer.Ordinal),
            ]);
        }
    }

    public Task<IReadOnlyList<VehicleAvailabilityInfo>> GetAvailabilityAsync(Guid categoryId, Period range, CancellationToken cancellationToken = default)
    {
        journal.Add("fleet.availability");
        Interlocked.Increment(ref _availabilityReads);
        if (NextAvailabilityFailure is { } failure)
        {
            NextAvailabilityFailure = null;
            throw failure;
        }

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<VehicleAvailabilityInfo>>(
            [
                .. _vehicles
                    .Where(v => v.Vehicle.CategoryId == categoryId && v.Vehicle.Status != VehicleStatusInfo.Sold)
                    .OrderBy(v => v.Vehicle.PlateNumber, StringComparer.Ordinal)
                    .Select(v => new VehicleAvailabilityInfo(
                        v.Vehicle.Id,
                        v.Vehicle.PlateNumber,
                        [.. v.Windows.Select(w => w.Intersect(range)).OfType<Period>().OrderBy(w => w.Start)])),
            ]);
        }
    }
}

/// <summary>Bookings of each category, turned into occupancy intervals with the basis it receives (as the Bookings module will).</summary>
internal sealed class FakeOccupancyReader(CallJournal journal) : IOccupancyReader
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, List<BookingOccupancy>> _bookings = [];
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    /// <summary>When set, a read takes its data at once but only returns once it completes (a load in flight across a commit).</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Completed when a read has taken its data.</summary>
    public TaskCompletionSource ReadTaken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OccupancyBasis? LastBasis { get; private set; }

    public Guid? LastExcludedBookingId { get; private set; }

    public Period? LastRange { get; private set; }

    public void Add(Guid categoryId, BookingOccupancy booking)
    {
        lock (_gate)
        {
            if (!_bookings.TryGetValue(categoryId, out var bookings))
            {
                bookings = [];
                _bookings.Add(categoryId, bookings);
            }

            bookings.Add(booking);
        }
    }

    public async Task<IReadOnlyList<OccupancyInterval>> GetOccupancyAsync(
        Guid categoryId,
        Period range,
        Guid? excludedBookingId,
        OccupancyBasis basis,
        CancellationToken cancellationToken = default)
    {
        journal.Add("occupancy");
        Interlocked.Increment(ref _reads);
        IReadOnlyList<OccupancyInterval> intervals;
        lock (_gate)
        {
            LastBasis = basis;
            LastExcludedBookingId = excludedBookingId;
            LastRange = range;
            var bookings = _bookings.GetValueOrDefault(categoryId) ?? [];
            intervals =
            [
                .. bookings
                    .Where(b => b.BookingId != excludedBookingId)
                    .Select(basis.IntervalOf)
                    .OfType<OccupancyInterval>()
                    .Where(i => i.Period.Overlaps(range)),
            ];
        }

        ReadTaken.TrySetResult();
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return intervals;
    }
}

/// <summary>Preparation buffer per agency (2 h by default); other settings return their default.</summary>
internal sealed class FakeSettingsReader : ISettingsReader
{
    public Dictionary<Guid, TimeSpan> BufferByAgency { get; } = [];

    public Task<T> GetAsync<T>(SettingDefinition<T> definition, Guid? agencyId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Key == WellKnownSettings.PreparationBuffer.Key
            && agencyId is { } agency
            && BufferByAgency.TryGetValue(agency, out var buffer))
        {
            return Task.FromResult((T)(object)buffer);
        }

        return Task.FromResult(definition.Default);
    }
}

/// <summary>Restrictions configured per (category, channel).</summary>
internal sealed class FakeChannelPolicy(CallJournal journal) : IChannelAvailabilityPolicy
{
    private readonly Dictionary<(Guid, SalesChannel), ChannelRestrictionsInfo> _restrictions = [];

    public void Set(Guid categoryId, SalesChannel channel, ChannelRestrictionsInfo restrictions) =>
        _restrictions[(categoryId, channel)] = restrictions;

    public Task<ChannelRestrictionsInfo> GetRestrictionsAsync(Guid categoryId, SalesChannel channel, Period range, CancellationToken cancellationToken = default)
    {
        journal.Add(string.Create(CultureInfo.InvariantCulture, $"policy.{channel}"));
        return Task.FromResult(_restrictions.GetValueOrDefault((categoryId, channel)) ?? ChannelRestrictionsInfo.None);
    }
}

/// <summary>Records the locks taken; like the SQL lock it refuses to run outside a transaction.</summary>
internal sealed class FakeAvailabilityLock(IUnitOfWork unitOfWork, CallJournal journal) : IAvailabilityLock
{
    public List<Guid> Acquired { get; } = [];

    public Result Outcome { get; set; } = Result.Success();

    public Task<Result> AcquireAsync(Guid categoryId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            throw new InvalidOperationException("The availability lock requires a transaction.");
        }

        Acquired.Add(categoryId);
        journal.Add(string.Create(CultureInfo.InvariantCulture, $"lock {categoryId}"));
        return Task.FromResult(Outcome);
    }
}

/// <summary>
/// A small agency (Tunis) with fakes for every input of the availability engine, at a fixed "now" (1 July 2027
/// 08:00 UTC, high season). Times are given in hours from now.
/// </summary>
internal sealed class AvailabilityWorld
{
    public static readonly DateTimeOffset Now = new(2027, 7, 1, 8, 0, 0, TimeSpan.Zero);

    public AvailabilityWorld()
    {
        Fleet = new FakeFleetQueries(Journal);
        Occupancy = new FakeOccupancyReader(Journal);
        Channels = new FakeChannelPolicy(Journal);
        Lock = new FakeAvailabilityLock(UnitOfWork, Journal);
    }

    public Guid AgencyId { get; } = Guid.CreateVersion7();

    public FakeTimeProvider Clock { get; } = new(Now);

    public CallJournal Journal { get; } = new();

    public FakeFleetQueries Fleet { get; }

    public FakeOccupancyReader Occupancy { get; }

    public FakeSettingsReader Settings { get; } = new();

    public FakeChannelPolicy Channels { get; }

    public InlineUnitOfWork UnitOfWork { get; } = new();

    public FakeAvailabilityLock Lock { get; }

    /// <summary>A window wide enough to mean "always rentable" (two years around now).</summary>
    public static Period Always => P(-24 * 365, 24 * 365);

    public static DateTimeOffset At(double hours) => Now.AddHours(hours);

    public static Period P(double fromHours, double toHours) => Period.Create(At(fromHours), At(toHours)).Value;

    public static BookingOccupancy Planned(Guid bookingId, Period rental, Guid? vehicleId = null) =>
        new(bookingId, OccupancyKind.Planned, rental, assignedVehicleId: vehicleId);

    public VehicleCategoryInfo Category(string acrissCode = "ECMR", int vehicles = 1, int displayOrder = 0, bool isActive = true)
    {
        var category = Fleet.AddCategory(AgencyId, acrissCode, displayOrder, isActive);
        for (var i = 1; i <= vehicles; i++)
        {
            Fleet.AddVehicle(category, string.Create(CultureInfo.InvariantCulture, $"{i:D3} TU {acrissCode}"), [Always]);
        }

        return category;
    }

    public Guid Book(VehicleCategoryInfo category, Period rental, Guid? vehicleId = null)
    {
        var bookingId = Guid.CreateVersion7();
        Occupancy.Add(category.Id, Planned(bookingId, rental, vehicleId));
        return bookingId;
    }

    public AvailabilitySnapshotLoader Loader() => new(Fleet, Occupancy, Channels, Settings, Clock);

    public AvailabilityGuard Guard() => new(UnitOfWork, Lock, Loader(), Fleet);

    /// <summary>The real registration (<c>AddBookingAvailability</c>) over the fakes.</summary>
    public ServiceProvider Services(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IFleetQueries>(Fleet);
        services.AddSingleton<IOccupancyReader>(Occupancy);
        services.AddSingleton<ISettingsReader>(Settings);
        services.AddSingleton<IChannelAvailabilityPolicy>(Channels);
        services.AddSingleton<IUnitOfWork>(UnitOfWork);
        services.AddSingleton<IAvailabilityLock>(Lock);
        configure?.Invoke(services);
        services.AddBookingAvailability();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
