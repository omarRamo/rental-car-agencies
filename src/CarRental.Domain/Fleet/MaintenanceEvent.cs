using CarRental.Domain.Common;

namespace CarRental.Domain.Fleet;

/// <summary>
/// A maintenance of a vehicle (planned, in progress, completed or cancelled) with its dated block <c>[start, end)</c>
/// during which the vehicle cannot be rented. A completed maintenance blocks until its actual completion; a cancelled one
/// blocks nothing.
/// </summary>
public sealed class MaintenanceEvent : Entity, IAgencyOwned
{
    public const int DescriptionMaxLength = 500;
    public const int ProviderMaxLength = 120;

    /// <summary>A block lasts one year at most (a longer immobilization is an out-of-service status).</summary>
    public static readonly TimeSpan MaxBlockDuration = TimeSpan.FromDays(366);

    private MaintenanceEvent()
    {
    }

    private MaintenanceEvent(Guid id, Vehicle vehicle, MaintenanceDetails details)
        : base(id)
    {
        VehicleId = vehicle.Id;
        AgencyId = vehicle.AgencyId;
        Type = details.Type;
        Description = details.Description;
        Provider = details.Provider;
        Cost = details.EstimatedCost;
        StartsAtUtc = details.StartsAt.ToUniversalTime();
        EndsAtUtc = details.EndsAt.ToUniversalTime();
        Status = MaintenanceStatus.Planned;
    }

    public Guid VehicleId { get; private set; }

    /// <summary>The agency of the vehicle (ADR-0067).</summary>
    public Guid AgencyId { get; private set; }

    public MaintenanceType Type { get; private set; }

    public string? Description { get; private set; }

    /// <summary>Garage or supplier.</summary>
    public string? Provider { get; private set; }

    public MaintenanceStatus Status { get; private set; }

    /// <summary>Start of the block (moved to the actual start when the work begins earlier).</summary>
    public DateTimeOffset StartsAtUtc { get; private set; }

    /// <summary>Planned (or expected) end of the block, excluded.</summary>
    public DateTimeOffset EndsAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public DateTimeOffset? CancelledAtUtc { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>Estimated cost while planned, actual cost once completed (VAT included).</summary>
    public Money? Cost { get; private set; }

    /// <summary>Mileage recorded when the maintenance was completed.</summary>
    public int? Mileage { get; private set; }

    public string? CompletionNotes { get; private set; }

    /// <summary>
    /// The period during which this maintenance makes the vehicle unavailable: <c>[start, end)</c> while planned or in
    /// progress, <c>[start, completion)</c> once completed, nothing when cancelled.
    /// </summary>
    public Period? GetBlock() => BlockOf(Status, StartsAtUtc, EndsAtUtc, CompletedAtUtc);

    /// <summary>The blocking rule of <see cref="GetBlock"/>, for queries that project the stored values.</summary>
    public static Period? BlockOf(MaintenanceStatus status, DateTimeOffset startsAt, DateTimeOffset endsAt, DateTimeOffset? completedAt) => status switch
    {
        MaintenanceStatus.Cancelled => null,
        MaintenanceStatus.Completed => completedAt is { } completed && completed > startsAt
            ? Period.Create(startsAt, completed).Value
            : null,
        _ => endsAt > startsAt ? Period.Create(startsAt, endsAt).Value : null,
    };

    /// <summary>The block as planned or expected, whatever the status.</summary>
    public Period GetPlannedBlock() => Period.Create(StartsAtUtc, EndsAtUtc).Value;

    internal static Result<MaintenanceDetails> Validate(MaintenanceDetails details)
    {
        if (!Enum.IsDefined(details.Type))
        {
            return FleetErrors.MaintenanceTypeInvalid;
        }

        var normalized = details with
        {
            Description = string.IsNullOrWhiteSpace(details.Description) ? null : details.Description.Trim(),
            Provider = string.IsNullOrWhiteSpace(details.Provider) ? null : details.Provider.Trim(),
        };

        if (normalized.Description?.Length > DescriptionMaxLength || normalized.Provider?.Length > ProviderMaxLength)
        {
            return FleetErrors.MaintenanceTextTooLong;
        }

        if (!IsValidBlock(normalized.StartsAt, normalized.EndsAt))
        {
            return FleetErrors.MaintenanceBlockInvalid;
        }

        return FleetAmounts.IsValid(normalized.EstimatedCost) ? normalized : FleetErrors.AmountInvalid;
    }

    internal static bool IsValidBlock(DateTimeOffset start, DateTimeOffset end) =>
        end > start && end - start <= MaxBlockDuration;

    internal static MaintenanceEvent Create(Vehicle vehicle, MaintenanceDetails validated) => new(NewId(), vehicle, validated);

    internal void Reschedule(DateTimeOffset start, DateTimeOffset end)
    {
        StartsAtUtc = start.ToUniversalTime();
        EndsAtUtc = end.ToUniversalTime();
    }

    internal void Start(DateTimeOffset now, DateTimeOffset end)
    {
        Status = MaintenanceStatus.InProgress;
        StartedAtUtc = now;
        if (now < StartsAtUtc)
        {
            StartsAtUtc = now;
        }

        EndsAtUtc = end.ToUniversalTime();
    }

    internal void Complete(DateTimeOffset completedAt, int? mileage, Money? cost, string? notes)
    {
        Status = MaintenanceStatus.Completed;
        StartedAtUtc ??= StartsAtUtc;
        CompletedAtUtc = completedAt;
        Mileage = mileage;
        Cost = cost ?? Cost;
        CompletionNotes = notes;
    }

    internal void Cancel(DateTimeOffset at, string reason)
    {
        Status = MaintenanceStatus.Cancelled;
        CancelledAtUtc = at;
        CancellationReason = reason;
    }
}

/// <summary>Data of a maintenance to schedule: type, block <c>[StartsAt, EndsAt)</c>, description, garage and estimated cost.</summary>
public sealed record MaintenanceDetails(
    MaintenanceType Type,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Description = null,
    string? Provider = null,
    Money? EstimatedCost = null);
