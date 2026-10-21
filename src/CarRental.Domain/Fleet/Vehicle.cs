using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Fleet;

/// <summary>
/// A vehicle of the fleet (aggregate): identity and plate number (unique per agency), category, mileage, status,
/// documents and deadlines, maintenance plans and dated maintenance blocks, economics, photos and a readable history.
/// Every change that affects its rentable windows raises <see cref="VehicleAvailabilityChanged"/> once.
/// </summary>
/// <remarks>
/// <see cref="Revision"/> is incremented by every change, so that the row (and its <c>rowversion</c>) is updated even
/// when only a child changes: two concurrent edits of the same vehicle cannot both succeed.
/// </remarks>
public sealed class Vehicle : AggregateRoot
{
    public const int PlateNumberMinLength = 2;
    public const int PlateNumberMaxLength = 20;
    public const int MakeMaxLength = 50;
    public const int ModelMaxLength = 60;
    public const int ColorMaxLength = 30;
    public const int ReasonMaxLength = 500;
    public const int BuyerMaxLength = 200;
    public const int MinYear = 1990;
    public const int MaxYear = 2100;
    public const int MaxMileage = 2_000_000;
    public const int MaxSeats = 9;
    public const int MaxBags = 10;
    public const int MaxPhotos = 12;

    /// <summary>Cancellation reason of the planned maintenances of a vehicle that is sold (shown translated).</summary>
    public const string CancelledBySaleReason = "vehicle.sold";

    private readonly List<VehicleDocument> _documents = [];
    private readonly List<MaintenancePlan> _maintenancePlans = [];
    private readonly List<MaintenanceEvent> _maintenanceEvents = [];
    private readonly List<VehicleCost> _costs = [];
    private readonly List<VehiclePhoto> _photos = [];
    private readonly List<VehicleHistoryEntry> _history = [];

    private Vehicle()
    {
    }

    private Vehicle(Guid id, Guid agencyId, Guid categoryId, VehicleDetails details, int mileage, ChangeStamp stamp)
        : base(id)
    {
        AgencyId = agencyId;
        CategoryId = categoryId;
        Apply(details);
        Mileage = mileage;
        MileageUpdatedAtUtc = stamp.At;
        Status = VehicleStatus.Active;
        StatusChangedAtUtc = stamp.At;
    }

    public Guid AgencyId { get; private set; }

    public Guid CategoryId { get; private set; }

    /// <summary>Normalized plate number (upper case, single spaces).</summary>
    public string PlateNumber { get; private set; } = null!;

    public string Make { get; private set; } = null!;

    public string Model { get; private set; } = null!;

    public int Year { get; private set; }

    public string? Color { get; private set; }

    public Transmission Transmission { get; private set; }

    public FuelType Fuel { get; private set; }

    public int Seats { get; private set; }

    public int LargeBags { get; private set; }

    public int SmallBags { get; private set; }

    /// <summary>Odometer reading in km: never decreases, except through a correction with a reason.</summary>
    public int Mileage { get; private set; }

    public DateTimeOffset MileageUpdatedAtUtc { get; private set; }

    /// <summary>Tunis date the vehicle joined the rental fleet: rentable from the start of that day.</summary>
    public DateOnly InServiceOn { get; private set; }

    /// <summary>Date of first registration (mise en circulation), as on the registration card.</summary>
    public DateOnly? FirstRegistrationOn { get; private set; }

    public VehicleStatus Status { get; private set; }

    public string? StatusReason { get; private set; }

    public DateTimeOffset StatusChangedAtUtc { get; private set; }

    /// <summary>Daily supplement (VAT included) to book this exact vehicle ("modèle garanti"); <see langword="null"/> when not offered.</summary>
    public Money? GuaranteedModelDailySupplement { get; private set; }

    public DateTimeOffset? SoldAtUtc { get; private set; }

    public Money? SalePrice { get; private set; }

    public string? Buyer { get; private set; }

    public VehicleEconomics? Economics { get; private set; }

    /// <summary>Incremented by every change (see remarks of the class).</summary>
    public int Revision { get; private set; }

    /// <summary>Sequence number of the last history line: the history is ordered by it, not by the clock.</summary>
    public int LastHistorySequence { get; private set; }

    public IReadOnlyList<VehicleDocument> Documents => _documents;

    public IReadOnlyList<MaintenancePlan> MaintenancePlans => _maintenancePlans;

    public IReadOnlyList<MaintenanceEvent> MaintenanceEvents => _maintenanceEvents;

    public IReadOnlyList<VehicleCost> Costs => _costs;

    public IReadOnlyList<VehiclePhoto> Photos => _photos;

    /// <summary>History lines added by this instance (the stored history is read by queries, not loaded with the aggregate).</summary>
    public IReadOnlyList<VehicleHistoryEntry> History => _history;

    /// <summary>The current document of each type.</summary>
    public IEnumerable<VehicleDocument> CurrentDocuments => _documents.Where(d => d.IsCurrent);

    /// <summary>End (exclusive) of the period covered by every mandatory document; <see langword="null"/> when one is missing.</summary>
    public DateTimeOffset? CompliantUntil => VehicleCompliance.CompliantUntil(CurrentDocuments.Select(d => (d.Type, d.ExpiresOn)));

    /// <summary>Dated blocks of the maintenances that are not cancelled.</summary>
    public IReadOnlyList<Period> GetMaintenanceBlocks() =>
        _maintenanceEvents.Select(e => e.GetBlock()).OfType<Period>().ToList();

    public VehicleAvailabilityProfile GetAvailabilityProfile() =>
        new(InServiceOn, Status, SoldAtUtc, CompliantUntil, GetMaintenanceBlocks());

    public static Result<Vehicle> Register(Guid agencyId, Guid categoryId, VehicleDetails details, int mileage, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(stamp);
        if (agencyId == Guid.Empty)
        {
            return FleetErrors.AgencyRequired;
        }

        if (categoryId == Guid.Empty)
        {
            return FleetErrors.CategoryNotFound;
        }

        var validated = Validate(details);
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        if (mileage is < 0 or > MaxMileage)
        {
            return FleetErrors.MileageInvalid;
        }

        var vehicle = new Vehicle(NewId(), agencyId, categoryId, validated.Value, mileage, stamp);
        vehicle.Record(VehicleHistoryKind.Registered, stamp, newValue: vehicle.PlateNumber);
        vehicle.Raise(new VehicleRegistered(vehicle.Id, agencyId, categoryId, vehicle.PlateNumber));
        vehicle.AvailabilityChanged();
        return vehicle;
    }

    /// <summary>Rentable windows within <paramref name="range"/> at <paramref name="now"/> (see <see cref="VehicleAvailability"/>).</summary>
    public IReadOnlyList<Period> AvailabilityWindows(Period range, DateTimeOffset now) =>
        VehicleAvailability.Windows(GetAvailabilityProfile(), range, now);

    public VehicleDocument? CurrentDocument(VehicleDocumentType type) => _documents.FirstOrDefault(d => d.IsCurrent && d.Type == type);

    public bool HasMaintenanceInProgress => _maintenanceEvents.Any(e => e.Status == MaintenanceStatus.InProgress);

    public Result UpdateDetails(VehicleDetails details, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(stamp);
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard;
        }

        var validated = Validate(details);
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        var inServiceChanged = validated.Value.InServiceOn != InServiceOn;
        Apply(validated.Value);
        Record(VehicleHistoryKind.DetailsUpdated, stamp, newValue: PlateNumber);
        Raise(new VehicleDetailsUpdated(Id, PlateNumber));
        if (inServiceChanged)
        {
            AvailabilityChanged();
        }

        return Result.Success();
    }

    /// <summary>Moves the vehicle to another category of the same agency (checked by the caller); both capacities change.</summary>
    public Result ChangeCategory(Guid categoryId, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard;
        }

        if (categoryId == Guid.Empty)
        {
            return FleetErrors.CategoryNotFound;
        }

        if (categoryId == CategoryId)
        {
            return FleetErrors.SameCategory;
        }

        var previous = CategoryId;
        CategoryId = categoryId;
        Record(VehicleHistoryKind.CategoryChanged, stamp, previousValue: previous.ToString(), newValue: categoryId.ToString());
        Raise(new VehicleCategoryChanged(Id, previous, categoryId));
        AvailabilityChanged(previous);
        return Result.Success();
    }

    /// <summary>
    /// Changes the status among <c>Active</c>, <c>Maintenance</c> and <c>OutOfService</c> (a sale goes through
    /// <see cref="Sell"/>). Leaving the active status needs a reason; returning to it is refused while a maintenance
    /// is in progress (complete it instead).
    /// </summary>
    public Result ChangeStatus(VehicleStatus status, string? reason, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard;
        }

        if (status is not (VehicleStatus.Active or VehicleStatus.Maintenance or VehicleStatus.OutOfService) || status == Status)
        {
            return FleetErrors.InvalidStatusTransition;
        }

        var normalizedReason = NormalizeReason(reason);
        if (normalizedReason.IsFailure)
        {
            return normalizedReason.Error;
        }

        if (status != VehicleStatus.Active && normalizedReason.Value is null)
        {
            return FleetErrors.ReasonRequired;
        }

        if (status == VehicleStatus.Active && HasMaintenanceInProgress)
        {
            return FleetErrors.MaintenanceInProgress;
        }

        SetStatus(status, normalizedReason.Value, stamp);
        return Result.Success();
    }

    /// <summary>
    /// Records an odometer reading: the mileage never decreases (use <see cref="CorrectMileage"/>), an equal reading
    /// changes nothing. <paramref name="reason"/> tells where the reading comes from in the history (e.g. the number of
    /// the rental file of a hand-over).
    /// </summary>
    public Result RecordMileage(int mileage, ChangeStamp stamp, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard;
        }

        if (mileage is < 0 or > MaxMileage)
        {
            return FleetErrors.MileageInvalid;
        }

        if (mileage < Mileage)
        {
            return FleetErrors.MileageDecrease;
        }

        var normalizedReason = NormalizeReason(reason);
        if (normalizedReason.IsFailure)
        {
            return normalizedReason.Error;
        }

        if (mileage > Mileage)
        {
            SetMileage(mileage, VehicleHistoryKind.MileageRecorded, normalizedReason.Value, stamp);
        }

        return Result.Success();
    }

    /// <summary>Fixes a wrong odometer reading (possibly downwards); the reason is kept in the history.</summary>
    public Result CorrectMileage(int mileage, string? reason, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard;
        }

        if (mileage is < 0 or > MaxMileage)
        {
            return FleetErrors.MileageInvalid;
        }

        if (mileage == Mileage)
        {
            return FleetErrors.MileageUnchanged;
        }

        var normalizedReason = NormalizeReason(reason);
        if (normalizedReason.IsFailure)
        {
            return normalizedReason.Error;
        }

        if (normalizedReason.Value is not { } why)
        {
            return FleetErrors.ReasonRequired;
        }

        SetMileage(mileage, VehicleHistoryKind.MileageCorrected, why, stamp);
        return Result.Success();
    }

    public Result SetGuaranteedModelSupplement(Money? dailySupplement, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard;
        }

        if (!FleetAmounts.IsValid(dailySupplement))
        {
            return FleetErrors.AmountInvalid;
        }

        if (dailySupplement == GuaranteedModelDailySupplement)
        {
            return Result.Success();
        }

        var previous = GuaranteedModelDailySupplement;
        GuaranteedModelDailySupplement = dailySupplement;
        Touch();
        Record(VehicleHistoryKind.SupplementChanged, stamp, previousValue: Format(previous), newValue: Format(dailySupplement));
        Raise(new GuaranteedModelSupplementChanged(Id, CategoryId, dailySupplement));
        return Result.Success();
    }

    /// <summary>
    /// Records the sale at <paramref name="soldAt"/> (not in the future): the vehicle leaves the fleet, its planned
    /// maintenances are cancelled. Refused while a maintenance is in progress.
    /// </summary>
    public Result Sell(DateTimeOffset soldAt, Money salePrice, string? buyer, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard;
        }

        var soldAtUtc = soldAt.ToUniversalTime();
        if (soldAtUtc > stamp.At || soldAtUtc < TunisTime.StartOfDay(InServiceOn))
        {
            return FleetErrors.SaleDateInvalid;
        }

        if (!FleetAmounts.IsValid(salePrice))
        {
            return FleetErrors.AmountInvalid;
        }

        var normalizedBuyer = string.IsNullOrWhiteSpace(buyer) ? null : buyer.Trim();
        if (normalizedBuyer?.Length > BuyerMaxLength)
        {
            return FleetErrors.BuyerTooLong;
        }

        if (HasMaintenanceInProgress)
        {
            return FleetErrors.MaintenanceInProgress;
        }

        foreach (var planned in _maintenanceEvents.Where(e => e.Status == MaintenanceStatus.Planned).ToList())
        {
            planned.Cancel(stamp.At, CancelledBySaleReason);
            Raise(new MaintenanceCancelled(Id, CategoryId, planned.Id));
        }

        SoldAtUtc = soldAtUtc;
        SalePrice = salePrice;
        Buyer = normalizedBuyer;
        var previous = Status;
        Status = VehicleStatus.Sold;
        StatusReason = null;
        StatusChangedAtUtc = stamp.At;
        Touch();
        Record(VehicleHistoryKind.Sold, stamp, subject: normalizedBuyer, previousValue: previous.ToString(), newValue: Format(salePrice));
        Raise(new VehicleStatusChanged(Id, CategoryId, previous, VehicleStatus.Sold, Reason: null));
        Raise(new VehicleSold(Id, CategoryId, soldAtUtc, salePrice));
        AvailabilityChanged();
        return Result.Success();
    }

    /// <summary>Records a document (first one or renewal): the current document of the type moves to the history.</summary>
    public Result<VehicleDocument> RecordDocument(VehicleDocumentDetails details, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(stamp);
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard.Error;
        }

        var validated = VehicleDocument.Validate(details);
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        var previous = CurrentDocument(details.Type);
        previous?.Supersede(stamp.At);
        var document = VehicleDocument.Create(Id, validated.Value, stamp.At);
        _documents.Add(document);
        Touch();
        Record(VehicleHistoryKind.DocumentRecorded, stamp, details.Type.ToString(), Format(previous?.ExpiresOn), Format(document.ExpiresOn));
        Raise(new VehicleDocumentRecorded(Id, CategoryId, document.Id, document.Type, document.ExpiresOn));
        if (VehicleCompliance.IsMandatory(document.Type))
        {
            AvailabilityChanged();
        }

        return document;
    }

    /// <summary>Corrects a typing error in the current document of a type (same type, no new history entry of renewal).</summary>
    public Result CorrectDocument(Guid documentId, VehicleDocumentDetails details, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(stamp);
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard;
        }

        var document = _documents.FirstOrDefault(d => d.Id == documentId);
        if (document is null)
        {
            return FleetErrors.DocumentNotFound;
        }

        if (!document.IsCurrent)
        {
            return FleetErrors.DocumentNotCurrent;
        }

        if (details.Type != document.Type)
        {
            return FleetErrors.DocumentTypeInvalid;
        }

        var validated = VehicleDocument.Validate(details);
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        var previousExpiry = document.ExpiresOn;
        document.Correct(validated.Value);
        Touch();
        Record(VehicleHistoryKind.DocumentCorrected, stamp, document.Type.ToString(), Format(previousExpiry), Format(document.ExpiresOn));
        Raise(new VehicleDocumentCorrected(Id, CategoryId, document.Id, document.Type, document.ExpiresOn));
        if (VehicleCompliance.IsMandatory(document.Type))
        {
            AvailabilityChanged();
        }

        return Result.Success();
    }

    /// <summary>Creates or replaces the maintenance plan of a type.</summary>
    public Result<MaintenancePlan> SetMaintenancePlan(MaintenancePlanDetails details, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(stamp);
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard.Error;
        }

        var validated = MaintenancePlan.Validate(details);
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        var plan = _maintenancePlans.FirstOrDefault(p => p.Type == details.Type);
        if (plan is null)
        {
            plan = MaintenancePlan.Create(Id, validated.Value, Mileage, stamp.Today);
            _maintenancePlans.Add(plan);
        }
        else
        {
            plan.Apply(validated.Value, Mileage, stamp.Today);
        }

        Touch();
        Record(VehicleHistoryKind.MaintenancePlanChanged, stamp, details.Type.ToString(), newValue: DescribeInterval(plan));
        return plan;
    }

    public Result RemoveMaintenancePlan(Guid planId, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard;
        }

        var plan = _maintenancePlans.FirstOrDefault(p => p.Id == planId);
        if (plan is null)
        {
            return FleetErrors.MaintenancePlanNotFound;
        }

        _maintenancePlans.Remove(plan);
        Touch();
        Record(VehicleHistoryKind.MaintenancePlanRemoved, stamp, plan.Type.ToString());
        return Result.Success();
    }

    /// <summary>Plans a maintenance: its block makes the vehicle unavailable; blocks of a vehicle never overlap.</summary>
    public Result<MaintenanceEvent> ScheduleMaintenance(MaintenanceDetails details, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(stamp);
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard.Error;
        }

        var validated = MaintenanceEvent.Validate(details);
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        var block = Period.Create(validated.Value.StartsAt, validated.Value.EndsAt).Value;
        if (Overlaps(block, exceptId: null))
        {
            return FleetErrors.MaintenanceOverlap;
        }

        var maintenance = MaintenanceEvent.Create(Id, validated.Value);
        _maintenanceEvents.Add(maintenance);
        Touch();
        Record(VehicleHistoryKind.MaintenanceScheduled, stamp, maintenance.Type.ToString(), newValue: Format(block));
        Raise(new MaintenanceScheduled(Id, CategoryId, maintenance.Id, maintenance.Type, block));
        AvailabilityChanged();
        return maintenance;
    }

    public Result RescheduleMaintenance(Guid maintenanceId, DateTimeOffset startsAt, DateTimeOffset endsAt, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var found = FindPlannedMaintenance(maintenanceId);
        if (found.IsFailure)
        {
            return found.Error;
        }

        if (!MaintenanceEvent.IsValidBlock(startsAt, endsAt))
        {
            return FleetErrors.MaintenanceBlockInvalid;
        }

        var maintenance = found.Value;
        var block = Period.Create(startsAt, endsAt).Value;
        if (Overlaps(block, maintenance.Id))
        {
            return FleetErrors.MaintenanceOverlap;
        }

        var previous = maintenance.GetPlannedBlock();
        maintenance.Reschedule(block.Start, block.End);
        Touch();
        Record(VehicleHistoryKind.MaintenanceRescheduled, stamp, maintenance.Type.ToString(), Format(previous), Format(block));
        Raise(new MaintenanceRescheduled(Id, CategoryId, maintenance.Id, block));
        AvailabilityChanged();
        return Result.Success();
    }

    /// <summary>
    /// The work begins: the block starts now if earlier than planned and ends at <paramref name="expectedEnd"/> (or as
    /// planned), which must be in the future; the vehicle goes to the <c>Maintenance</c> status.
    /// </summary>
    public Result StartMaintenance(Guid maintenanceId, DateTimeOffset? expectedEnd, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var found = FindPlannedMaintenance(maintenanceId);
        if (found.IsFailure)
        {
            return found.Error;
        }

        if (HasMaintenanceInProgress)
        {
            return FleetErrors.MaintenanceAlreadyInProgress;
        }

        var maintenance = found.Value;
        var end = (expectedEnd ?? maintenance.EndsAtUtc).ToUniversalTime();
        if (end <= stamp.At)
        {
            return FleetErrors.MaintenanceEndInPast;
        }

        var start = stamp.At < maintenance.StartsAtUtc ? stamp.At : maintenance.StartsAtUtc;
        if (!MaintenanceEvent.IsValidBlock(start, end))
        {
            return FleetErrors.MaintenanceBlockInvalid;
        }

        if (Overlaps(Period.Create(start, end).Value, maintenance.Id))
        {
            return FleetErrors.MaintenanceOverlap;
        }

        maintenance.Start(stamp.At, end);
        Touch();
        Record(VehicleHistoryKind.MaintenanceStarted, stamp, maintenance.Type.ToString(), newValue: Format(maintenance.GetPlannedBlock()));
        Raise(new MaintenanceStarted(Id, CategoryId, maintenance.Id, maintenance.GetPlannedBlock()));
        if (Status != VehicleStatus.Maintenance)
        {
            SetStatus(VehicleStatus.Maintenance, maintenance.Description ?? maintenance.Type.ToString(), stamp);
        }

        AvailabilityChanged();
        return Result.Success();
    }

    /// <summary>
    /// The work is done at <paramref name="completedAt"/> (now by default; after the start and not in the future): the
    /// block ends then, the plan of the same type restarts from it and the vehicle becomes active again when it was in
    /// maintenance. A higher <paramref name="mileage"/> updates the odometer.
    /// </summary>
    public Result CompleteMaintenance(Guid maintenanceId, DateTimeOffset? completedAt, int? mileage, Money? cost, string? notes, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard;
        }

        var maintenance = _maintenanceEvents.FirstOrDefault(e => e.Id == maintenanceId);
        if (maintenance is null)
        {
            return FleetErrors.MaintenanceNotFound;
        }

        if (maintenance.Status is not (MaintenanceStatus.Planned or MaintenanceStatus.InProgress))
        {
            return FleetErrors.MaintenanceInvalidTransition;
        }

        var completion = (completedAt ?? stamp.At).ToUniversalTime();
        if (completion <= maintenance.StartsAtUtc || completion > stamp.At)
        {
            return FleetErrors.MaintenanceNotStarted;
        }

        if (mileage is < 0 or > MaxMileage)
        {
            return FleetErrors.MileageInvalid;
        }

        if (!FleetAmounts.IsValid(cost))
        {
            return FleetErrors.AmountInvalid;
        }

        var normalizedNotes = NormalizeReason(notes);
        if (normalizedNotes.IsFailure)
        {
            return normalizedNotes.Error;
        }

        maintenance.Complete(completion, mileage, cost, normalizedNotes.Value);
        if (mileage is { } reading && reading > Mileage)
        {
            SetMileage(reading, VehicleHistoryKind.MileageRecorded, reason: null, stamp);
        }

        _maintenancePlans.FirstOrDefault(p => p.Type == maintenance.Type)?.MarkDone(TunisTime.DateOf(completion), mileage ?? Mileage);
        Touch();
        Record(VehicleHistoryKind.MaintenanceCompleted, stamp, maintenance.Type.ToString(), newValue: Format(maintenance.Cost), reason: normalizedNotes.Value);
        Raise(new MaintenanceCompleted(Id, CategoryId, maintenance.Id, completion));
        if (Status == VehicleStatus.Maintenance && !HasMaintenanceInProgress)
        {
            SetStatus(VehicleStatus.Active, reason: null, stamp);
        }

        AvailabilityChanged();
        return Result.Success();
    }

    public Result CancelMaintenance(Guid maintenanceId, string? reason, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var found = FindPlannedMaintenance(maintenanceId);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var normalizedReason = NormalizeReason(reason);
        if (normalizedReason.IsFailure)
        {
            return normalizedReason.Error;
        }

        if (normalizedReason.Value is not { } why)
        {
            return FleetErrors.ReasonRequired;
        }

        var maintenance = found.Value;
        maintenance.Cancel(stamp.At, why);
        Touch();
        Record(VehicleHistoryKind.MaintenanceCancelled, stamp, maintenance.Type.ToString(), Format(maintenance.GetPlannedBlock()), reason: why);
        Raise(new MaintenanceCancelled(Id, CategoryId, maintenance.Id));
        AvailabilityChanged();
        return Result.Success();
    }

    /// <summary>Records or replaces the economic data (also allowed after the sale, to complete the file).</summary>
    public Result SetEconomics(VehicleEconomicsDetails details, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(stamp);
        var validated = VehicleEconomics.Validate(details);
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        if (Economics is null)
        {
            Economics = VehicleEconomics.Create(Id, validated.Value);
        }
        else
        {
            Economics.Apply(validated.Value);
        }

        Touch();
        Record(VehicleHistoryKind.EconomicsUpdated, stamp, details.Financing.ToString(), newValue: Format(details.PurchasePrice));
        return Result.Success();
    }

    public Result<VehicleCost> AddCost(DateOnly incurredOn, string label, Money amount, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var cost = VehicleCost.Create(Id, incurredOn, label, amount);
        if (cost.IsFailure)
        {
            return cost.Error;
        }

        _costs.Add(cost.Value);
        Touch();
        Record(VehicleHistoryKind.CostAdded, stamp, cost.Value.Label, newValue: Format(amount));
        return cost.Value;
    }

    public Result RemoveCost(Guid costId, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var cost = _costs.FirstOrDefault(c => c.Id == costId);
        if (cost is null)
        {
            return FleetErrors.CostNotFound;
        }

        _costs.Remove(cost);
        Touch();
        Record(VehicleHistoryKind.CostRemoved, stamp, cost.Label, Format(cost.Amount));
        return Result.Success();
    }

    /// <summary>Attaches a stored photo (<paramref name="photoId"/> is the id used in its storage path).</summary>
    public Result<VehiclePhoto> AddPhoto(Guid photoId, StoredPhoto photo, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(stamp);
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard.Error;
        }

        if (_photos.Count >= MaxPhotos)
        {
            return FleetErrors.TooManyPhotos;
        }

        var added = VehiclePhoto.Create(photoId, Id, photo, stamp.At);
        _photos.Add(added);
        Touch();
        Record(VehicleHistoryKind.PhotoAdded, stamp);
        return added;
    }

    /// <summary>Detaches a photo and returns it, so that the caller deletes the stored file after the commit.</summary>
    public Result<VehiclePhoto> RemovePhoto(Guid photoId, ChangeStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var photo = _photos.FirstOrDefault(p => p.Id == photoId);
        if (photo is null)
        {
            return FleetErrors.PhotoNotFound;
        }

        _photos.Remove(photo);
        Touch();
        Record(VehicleHistoryKind.PhotoRemoved, stamp);
        return photo;
    }

    /// <summary>Due state of every maintenance plan on <paramref name="today"/>.</summary>
    public IReadOnlyList<(MaintenancePlan Plan, MaintenanceDue Due)> MaintenanceDues(DateOnly today, MaintenanceDueMargins margins) =>
        _maintenancePlans.Select(p => (p, p.ComputeDue(Mileage, today, margins))).ToList();

    private static Result<VehicleDetails> Validate(VehicleDetails details)
    {
        var plate = PlateNumbers.Normalize(details.PlateNumber);
        if (plate.IsFailure)
        {
            return plate.Error;
        }

        var normalized = details with
        {
            PlateNumber = plate.Value,
            Make = details.Make?.Trim() ?? string.Empty,
            Model = details.Model?.Trim() ?? string.Empty,
            Color = string.IsNullOrWhiteSpace(details.Color) ? null : details.Color.Trim(),
        };

        if (normalized.Make.Length is 0 or > MakeMaxLength)
        {
            return FleetErrors.MakeInvalid;
        }

        if (normalized.Model.Length is 0 or > ModelMaxLength)
        {
            return FleetErrors.ModelInvalid;
        }

        if (normalized.Year is < MinYear or > MaxYear)
        {
            return FleetErrors.YearInvalid;
        }

        if (normalized.Color?.Length > ColorMaxLength)
        {
            return FleetErrors.ColorInvalid;
        }

        if (!Enum.IsDefined(normalized.Transmission))
        {
            return FleetErrors.TransmissionInvalid;
        }

        if (!Enum.IsDefined(normalized.Fuel))
        {
            return FleetErrors.FuelInvalid;
        }

        if (normalized.Seats is < 1 or > MaxSeats
            || normalized.LargeBags is < 0 or > MaxBags
            || normalized.SmallBags is < 0 or > MaxBags)
        {
            return FleetErrors.VehicleCapacityInvalid;
        }

        if (normalized.FirstRegistrationOn is { } firstRegistration && normalized.InServiceOn < firstRegistration)
        {
            return FleetErrors.InServiceDateInvalid;
        }

        return normalized;
    }

    private static Result<string?> NormalizeReason(string? reason)
    {
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return trimmed?.Length > ReasonMaxLength ? FleetErrors.ReasonTooLong : trimmed;
    }

    private static string? Format(Money? amount) =>
        amount?.Amount.ToString("F3", CultureInfo.InvariantCulture);

    private static string? Format(DateOnly? date) => date?.ToString("O", CultureInfo.InvariantCulture);

    private static string Format(Period block) =>
        string.Create(CultureInfo.InvariantCulture, $"{block.Start:O}/{block.End:O}");

    private static string DescribeInterval(MaintenancePlan plan) =>
        string.Create(CultureInfo.InvariantCulture, $"{plan.IntervalKm?.ToString(CultureInfo.InvariantCulture) ?? "-"} km/{plan.IntervalMonths?.ToString(CultureInfo.InvariantCulture) ?? "-"} months");

    private Result EnsureNotSold() => Status == VehicleStatus.Sold ? FleetErrors.VehicleSold : Result.Success();

    private Result<MaintenanceEvent> FindPlannedMaintenance(Guid maintenanceId)
    {
        var guard = EnsureNotSold();
        if (guard.IsFailure)
        {
            return guard.Error;
        }

        var maintenance = _maintenanceEvents.FirstOrDefault(e => e.Id == maintenanceId);
        if (maintenance is null)
        {
            return FleetErrors.MaintenanceNotFound;
        }

        return maintenance.Status == MaintenanceStatus.Planned ? maintenance : FleetErrors.MaintenanceInvalidTransition;
    }

    private bool Overlaps(Period block, Guid? exceptId) =>
        _maintenanceEvents.Any(e => e.Id != exceptId && e.GetBlock() is { } other && other.Overlaps(block));

    private void Apply(VehicleDetails details)
    {
        PlateNumber = details.PlateNumber;
        Make = details.Make;
        Model = details.Model;
        Year = details.Year;
        Color = details.Color;
        Transmission = details.Transmission;
        Fuel = details.Fuel;
        Seats = details.Seats;
        LargeBags = details.LargeBags;
        SmallBags = details.SmallBags;
        InServiceOn = details.InServiceOn;
        FirstRegistrationOn = details.FirstRegistrationOn;
        Touch();
    }

    private void SetStatus(VehicleStatus status, string? reason, ChangeStamp stamp)
    {
        var previous = Status;
        Status = status;
        StatusReason = reason;
        StatusChangedAtUtc = stamp.At;
        Touch();
        Record(VehicleHistoryKind.StatusChanged, stamp, previousValue: previous.ToString(), newValue: status.ToString(), reason: reason);
        Raise(new VehicleStatusChanged(Id, CategoryId, previous, status, reason));
        AvailabilityChanged();
    }

    private void SetMileage(int mileage, VehicleHistoryKind kind, string? reason, ChangeStamp stamp)
    {
        var previous = Mileage;
        Mileage = mileage;
        MileageUpdatedAtUtc = stamp.At;
        Touch();
        Record(
            kind,
            stamp,
            previousValue: previous.ToString(CultureInfo.InvariantCulture),
            newValue: mileage.ToString(CultureInfo.InvariantCulture),
            reason: reason);
        Raise(new VehicleMileageRecorded(Id, previous, mileage, kind == VehicleHistoryKind.MileageCorrected));
    }

    private void Touch() => Revision++;

    private void Record(
        VehicleHistoryKind kind,
        ChangeStamp stamp,
        string? subject = null,
        string? previousValue = null,
        string? newValue = null,
        string? reason = null)
    {
        LastHistorySequence++;
        _history.Add(VehicleHistoryEntry.Create(Id, LastHistorySequence, kind, stamp, subject, previousValue, newValue, reason));
    }

    /// <summary>Raises <see cref="VehicleAvailabilityChanged"/> once per operation (and per previous category).</summary>
    private void AvailabilityChanged(Guid? previousCategoryId = null)
    {
        if (DomainEvents.OfType<VehicleAvailabilityChanged>().Any(e => e.PreviousCategoryId == previousCategoryId && e.CategoryId == CategoryId))
        {
            return;
        }

        Raise(new VehicleAvailabilityChanged(Id, AgencyId, CategoryId, previousCategoryId));
    }
}

/// <summary>Identity and technical data of a vehicle (the plate number is normalized).</summary>
public sealed record VehicleDetails(
    string PlateNumber,
    string Make,
    string Model,
    int Year,
    string? Color,
    Transmission Transmission,
    FuelType Fuel,
    int Seats,
    int LargeBags,
    int SmallBags,
    DateOnly InServiceOn,
    DateOnly? FirstRegistrationOn = null);
