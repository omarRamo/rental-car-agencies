using CarRental.Domain.Common;
using CarRental.Domain.Fleet;

namespace CarRental.Application.Fleet;

/// <summary>Severity of a deadline for the back office badges: orange when due soon, red when critical, expired or missing.</summary>
public enum DeadlineState
{
    Ok = 0,

    /// <summary>Expires within 30 days.</summary>
    DueSoon = 1,

    /// <summary>Expires within 7 days.</summary>
    Critical = 2,

    Expired = 3,

    /// <summary>A mandatory document is missing: the vehicle cannot be rented at all.</summary>
    Missing = 4,
}

public static class DeadlineStates
{
    public static DeadlineState Of(DateOnly? expiresOn, DateOnly today) => VehicleCompliance.LevelOf(expiresOn, today) switch
    {
        null => DeadlineState.Ok,
        DeadlineAlertLevel.Expired => DeadlineState.Expired,
        DeadlineAlertLevel.Within7Days => DeadlineState.Critical,
        _ => DeadlineState.DueSoon,
    };

    /// <summary>The most urgent deadline among the mandatory documents (a missing one first, then the earliest expiry).</summary>
    public static VehicleDeadline Worst(IEnumerable<VehicleDocument> currentDocuments, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(currentDocuments);
        var byType = currentDocuments.Where(d => d.IsCurrent).ToDictionary(d => d.Type);
        var missing = VehicleCompliance.MandatoryTypes.FirstOrDefault(t => !byType.TryGetValue(t, out var d) || d.ExpiresOn is null, (VehicleDocumentType)0);
        if (missing != 0)
        {
            return new VehicleDeadline(missing, null, null, DeadlineState.Missing);
        }

        var earliest = VehicleCompliance.MandatoryTypes.Select(t => byType[t]).MinBy(d => d.ExpiresOn)!;
        return new VehicleDeadline(earliest.Type, earliest.ExpiresOn, DaysLeft(earliest.ExpiresOn, today), Of(earliest.ExpiresOn, today));
    }

    public static int? DaysLeft(DateOnly? expiresOn, DateOnly today) => expiresOn is { } date ? date.DayNumber - today.DayNumber : null;
}

/// <summary>A deadline of a vehicle: document type, expiry (none when missing), days left and severity.</summary>
public sealed record VehicleDeadline(VehicleDocumentType Type, DateOnly? ExpiresOn, int? DaysLeft, DeadlineState State);

// Categories
public sealed record CategoryAdminItem(
    Guid Id,
    Guid AgencyId,
    string Code,
    string Name,
    string ExampleModel,
    int Seats,
    int Doors,
    int LargeBags,
    int SmallBags,
    Transmission Transmission,
    FuelType Fuel,
    bool AirConditioning,
    Money Deposit,
    Money ReducedDeposit,
    string? PhotoUrl,
    int DisplayOrder,
    bool IsActive,
    int VehicleCount);

/// <summary>Category form. <paramref name="AgencyId"/> may be omitted when the platform has a single agency.</summary>
public sealed record CategoryForm(
    string Code,
    string Name,
    string ExampleModel,
    int Seats,
    int Doors,
    int LargeBags,
    int SmallBags,
    FuelType Fuel,
    decimal Deposit,
    decimal ReducedDeposit,
    int DisplayOrder,
    Guid? AgencyId = null);

// Vehicles
public sealed record VehicleListFilter
{
    public Guid? CategoryId { get; init; }

    public VehicleStatus? Status { get; init; }

    public DeadlineState? Deadline { get; init; }

    /// <summary>Part of the plate number, make or model.</summary>
    public string? Search { get; init; }

    /// <summary>Sold vehicles are hidden unless asked (or filtered by the sold status).</summary>
    public bool IncludeSold { get; init; }
}

public sealed record VehicleListItem(
    Guid Id,
    string PlateNumber,
    string Make,
    string Model,
    int Year,
    Guid CategoryId,
    string CategoryCode,
    string CategoryName,
    VehicleStatus Status,
    string? StatusReason,
    int Mileage,
    VehicleDeadline Deadline,
    bool MaintenanceDue);

/// <summary>Identity form of a vehicle (registration and edition; the mileage is recorded separately).</summary>
public sealed record VehicleForm(
    Guid CategoryId,
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
    DateOnly? FirstRegistrationOn,
    decimal? GuaranteedModelDailySupplement);

public sealed record SaleForm(DateTimeOffset SoldAt, decimal Price, string? Buyer);

public sealed record DocumentForm(
    VehicleDocumentType Type,
    string Number,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    string? Provider,
    decimal? Cost,
    string? Notes);

public sealed record EconomicsForm(
    decimal PurchasePrice,
    DateOnly PurchasedOn,
    FinancingType Financing,
    decimal? MonthlyPayment,
    int? FinancingMonths,
    int DepreciationYears,
    decimal ResidualValue);

public sealed record CostForm(DateOnly IncurredOn, string Label, decimal Amount);

public sealed record MaintenancePlanForm(
    MaintenanceType Type,
    int? IntervalKm,
    int? IntervalMonths,
    DateOnly? LastDoneOn,
    int? LastDoneMileage,
    string? Label);

public sealed record MaintenanceForm(
    MaintenanceType Type,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Description,
    string? Provider,
    decimal? EstimatedCost);

public sealed record MaintenanceCompletionForm(DateTimeOffset? CompletedAt, int? Mileage, decimal? Cost, string? Notes);

/// <summary>Everything the vehicle page shows (one read per display).</summary>
public sealed record VehicleDetail(
    Guid Id,
    Guid AgencyId,
    Guid CategoryId,
    string CategoryCode,
    string CategoryName,
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
    int Mileage,
    DateTimeOffset MileageUpdatedAtUtc,
    DateOnly InServiceOn,
    DateOnly? FirstRegistrationOn,
    VehicleStatus Status,
    string? StatusReason,
    DateTimeOffset StatusChangedAtUtc,
    Money? GuaranteedModelDailySupplement,
    DateTimeOffset? SoldAtUtc,
    Money? SalePrice,
    string? Buyer,
    DateTimeOffset? CompliantUntil,
    VehicleDeadline Deadline,
    IReadOnlyList<Period> AvailabilityNext30Days,
    IReadOnlyList<VehicleDocumentItem> Documents,
    IReadOnlyList<MaintenancePlanItem> MaintenancePlans,
    IReadOnlyList<MaintenanceItem> Maintenances,
    VehicleEconomicsItem? Economics,
    IReadOnlyList<VehicleCostItem> Costs,
    IReadOnlyList<VehiclePhotoItem> Photos,
    IReadOnlyList<VehicleHistoryItem> History);

public sealed record VehicleDocumentItem(
    Guid Id,
    VehicleDocumentType Type,
    string Number,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    string? Provider,
    Money? Cost,
    string? Notes,
    bool IsCurrent,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? SupersededAtUtc,
    int? DaysLeft,
    DeadlineState State);

public sealed record MaintenancePlanItem(
    Guid Id,
    MaintenanceType Type,
    string? Label,
    int? IntervalKm,
    int? IntervalMonths,
    DateOnly LastDoneOn,
    int LastDoneMileage,
    MaintenanceDue Due);

public sealed record MaintenanceItem(
    Guid Id,
    MaintenanceType Type,
    MaintenanceStatus Status,
    string? Description,
    string? Provider,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? CancellationReason,
    Money? Cost,
    int? Mileage,
    string? CompletionNotes);

public sealed record VehicleEconomicsItem(
    Money PurchasePrice,
    DateOnly PurchasedOn,
    FinancingType Financing,
    Money? MonthlyPayment,
    int? FinancingMonths,
    int DepreciationYears,
    Money ResidualValue,
    Money DepreciableAmount,
    DateOnly DepreciationEndsOn,
    Money AccumulatedDepreciation,
    Money BookValue,
    IReadOnlyList<MonthlyEconomics> Schedule);

/// <summary>Depreciation and financing instalment of a calendar month (profitability reports).</summary>
public sealed record MonthlyEconomics(int Year, int Month, Money Depreciation, Money Financing);

public sealed record VehicleCostItem(Guid Id, DateOnly IncurredOn, string Label, Money Amount);

public sealed record VehiclePhotoItem(Guid Id, string Url, string ContentType, long Length, DateTimeOffset UploadedAtUtc);

public sealed record VehicleHistoryItem(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    VehicleHistoryKind Kind,
    string? Actor,
    string? Subject,
    string? PreviousValue,
    string? NewValue,
    string? Reason);

// Deadlines and dashboard
public sealed record DeadlineItem(
    Guid VehicleId,
    string PlateNumber,
    string Make,
    string Model,
    string CategoryCode,
    VehicleStatus Status,
    VehicleDocumentType Type,
    Guid? DocumentId,
    string? Number,
    DateOnly? ExpiresOn,
    int? DaysLeft,
    DeadlineState State);

public sealed record UnavailableVehicleItem(
    Guid VehicleId,
    string PlateNumber,
    string Make,
    string Model,
    VehicleStatus Status,
    string? Reason,
    DateTimeOffset Since,
    DateTimeOffset? ExpectedBackAtUtc);

/// <summary>Fleet part of the daily dashboard: urgent deadlines and vehicles out of the rental pool.</summary>
public sealed record FleetDashboard(
    IReadOnlyList<DeadlineItem> Alerts,
    int AlertCount,
    IReadOnlyList<UnavailableVehicleItem> Unavailable,
    int VehicleCount,
    int AvailableNowCount);
