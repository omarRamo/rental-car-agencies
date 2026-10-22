using CarRental.Domain.Common;

namespace CarRental.Domain.Fleet;

/// <summary>
/// A recurring maintenance of a vehicle (e.g. oil change every 10 000 km or 12 months, whichever comes first), with
/// the last time it was done. One plan per maintenance type; completing a maintenance of that type moves the baseline.
/// </summary>
public sealed class MaintenancePlan : Entity, IAgencyOwned
{
    public const int MinIntervalKm = 100;
    public const int MaxIntervalKm = 200_000;
    public const int MaxIntervalMonths = 120;
    public const int LabelMaxLength = 100;

    private MaintenancePlan()
    {
    }

    private MaintenancePlan(Guid id, Vehicle vehicle, MaintenanceType type)
        : base(id)
    {
        VehicleId = vehicle.Id;
        AgencyId = vehicle.AgencyId;
        Type = type;
    }

    public Guid VehicleId { get; private set; }

    /// <summary>The agency of the vehicle (ADR-0067).</summary>
    public Guid AgencyId { get; private set; }

    public MaintenanceType Type { get; private set; }

    public string? Label { get; private set; }

    public int? IntervalKm { get; private set; }

    public int? IntervalMonths { get; private set; }

    public DateOnly LastDoneOn { get; private set; }

    public int LastDoneMileage { get; private set; }

    public int? NextDueMileage => IntervalKm is { } km ? LastDoneMileage + km : null;

    public DateOnly? NextDueOn => IntervalMonths is { } months ? LastDoneOn.AddMonths(months) : null;

    /// <summary>
    /// Due state on <paramref name="today"/> at <paramref name="currentMileage"/>: overdue once the distance or the date is
    /// reached, due soon within the margins, otherwise ok.
    /// </summary>
    public MaintenanceDue ComputeDue(int currentMileage, DateOnly today, MaintenanceDueMargins margins)
    {
        ArgumentNullException.ThrowIfNull(margins);
        int? remainingKm = NextDueMileage is { } dueKm ? dueKm - currentMileage : null;
        int? remainingDays = NextDueOn is { } dueOn ? dueOn.DayNumber - today.DayNumber : null;

        var state = (remainingKm, remainingDays) switch
        {
            ( <= 0, _) or (_, < 0) => MaintenanceDueState.Overdue,
            ({ } km, _) when km <= margins.Kilometers => MaintenanceDueState.DueSoon,
            (_, { } days) when days <= margins.Days => MaintenanceDueState.DueSoon,
            _ => MaintenanceDueState.Ok,
        };

        return new MaintenanceDue(NextDueMileage, NextDueOn, remainingKm, remainingDays, state);
    }

    internal static Result<MaintenancePlanDetails> Validate(MaintenancePlanDetails details)
    {
        if (!Enum.IsDefined(details.Type))
        {
            return FleetErrors.MaintenanceTypeInvalid;
        }

        if ((details.IntervalKm is null && details.IntervalMonths is null)
            || details.IntervalKm is < MinIntervalKm or > MaxIntervalKm
            || details.IntervalMonths is < 1 or > MaxIntervalMonths
            || details.LastDoneMileage is < 0 or > Vehicle.MaxMileage)
        {
            return FleetErrors.MaintenancePlanInvalid;
        }

        var label = string.IsNullOrWhiteSpace(details.Label) ? null : details.Label.Trim();
        if (label?.Length > LabelMaxLength)
        {
            return FleetErrors.MaintenancePlanInvalid;
        }

        return details with { Label = label };
    }

    /// <summary>A plan of <paramref name="vehicle"/>; the baseline defaults to its current mileage and <paramref name="today"/>.</summary>
    internal static MaintenancePlan Create(Vehicle vehicle, MaintenancePlanDetails validated, DateOnly today)
    {
        var plan = new MaintenancePlan(NewId(), vehicle, validated.Type);
        plan.Apply(validated, vehicle.Mileage, today);
        return plan;
    }

    internal void Apply(MaintenancePlanDetails validated, int currentMileage, DateOnly today)
    {
        Label = validated.Label;
        IntervalKm = validated.IntervalKm;
        IntervalMonths = validated.IntervalMonths;
        LastDoneOn = validated.LastDoneOn ?? today;
        LastDoneMileage = validated.LastDoneMileage ?? currentMileage;
    }

    /// <summary>A maintenance of this type was done: the next due distance and date start from it (never backwards).</summary>
    internal void MarkDone(DateOnly on, int mileage)
    {
        if (on > LastDoneOn)
        {
            LastDoneOn = on;
        }

        if (mileage > LastDoneMileage)
        {
            LastDoneMileage = mileage;
        }
    }
}

/// <summary>Data of a maintenance plan; the baseline defaults to the current mileage and today.</summary>
public sealed record MaintenancePlanDetails(
    MaintenanceType Type,
    int? IntervalKm,
    int? IntervalMonths,
    DateOnly? LastDoneOn = null,
    int? LastDoneMileage = null,
    string? Label = null);

/// <summary>How early a maintenance is reported as due soon (settings <c>fleet.maintenance_due_soon_*</c>).</summary>
public sealed record MaintenanceDueMargins(int Kilometers, int Days)
{
    public static MaintenanceDueMargins Default { get; } = new(1000, 30);
}

/// <summary>Next due distance and date of a plan, what remains, and the resulting state.</summary>
public sealed record MaintenanceDue(
    int? NextDueMileage,
    DateOnly? NextDueOn,
    int? RemainingKm,
    int? RemainingDays,
    MaintenanceDueState State);
