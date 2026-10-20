namespace CarRental.Domain.Fleet;

/// <summary>Gearbox of a category or vehicle (same values as <c>TransmissionInfo</c> in the Fleet contract).</summary>
public enum Transmission
{
    Manual = 1,
    Automatic = 2,
}

/// <summary>Fuel or energy (same values as <c>FuelTypeInfo</c> in the Fleet contract).</summary>
public enum FuelType
{
    Petrol = 1,
    Diesel = 2,
    Hybrid = 3,
    Electric = 4,
    Lpg = 5,
}

/// <summary>
/// Operational status of a vehicle (same values as <c>VehicleStatusInfo</c>). <see cref="Maintenance"/> and
/// <see cref="OutOfService"/> make the vehicle unavailable from now on, without end, unless a dated maintenance block
/// covers the current instant; <see cref="Sold"/> is terminal.
/// </summary>
public enum VehicleStatus
{
    Active = 1,
    Maintenance = 2,
    OutOfService = 3,
    Sold = 4,
}

/// <summary>Documents and deadlines of a vehicle. Insurance, road tax (vignette) and technical inspection are mandatory.</summary>
public enum VehicleDocumentType
{
    Insurance = 1,

    /// <summary>Vignette (annual road tax).</summary>
    RoadTax = 2,

    /// <summary>Visite technique.</summary>
    TechnicalInspection = 3,

    /// <summary>Carte grise; usually without expiry date.</summary>
    RegistrationCard = 4,
}

public enum MaintenanceType
{
    /// <summary>Vidange.</summary>
    OilChange = 1,
    Tyres = 2,
    Brakes = 3,
    Repair = 4,
    Other = 5,
}

public enum MaintenanceStatus
{
    Planned = 1,
    InProgress = 2,
    Completed = 3,
    Cancelled = 4,
}

public enum FinancingType
{
    Cash = 1,
    Credit = 2,
    Leasing = 3,
}

/// <summary>Kind of a line of the vehicle history (readable timeline shown in the back office).</summary>
public enum VehicleHistoryKind
{
    Registered = 1,
    DetailsUpdated = 2,
    CategoryChanged = 3,
    StatusChanged = 4,
    MileageRecorded = 5,
    MileageCorrected = 6,
    DocumentRecorded = 7,
    DocumentCorrected = 8,
    MaintenancePlanChanged = 9,
    MaintenancePlanRemoved = 10,
    MaintenanceScheduled = 11,
    MaintenanceRescheduled = 12,
    MaintenanceStarted = 13,
    MaintenanceCompleted = 14,
    MaintenanceCancelled = 15,
    EconomicsUpdated = 16,
    CostAdded = 17,
    CostRemoved = 18,
    PhotoAdded = 19,
    PhotoRemoved = 20,
    SupplementChanged = 21,
    Sold = 22,
}

/// <summary>
/// Urgency of a document deadline (the value is the number of days of the threshold): alerts are sent at 30, 15 and
/// 7 days before the expiry date, and once the document has expired.
/// </summary>
public enum DeadlineAlertLevel
{
    Expired = 0,
    Within7Days = 7,
    Within15Days = 15,
    Within30Days = 30,
}

/// <summary>State of the planned maintenance of a vehicle (whichever of the distance or the date comes first).</summary>
public enum MaintenanceDueState
{
    Ok = 1,
    DueSoon = 2,
    Overdue = 3,
}
