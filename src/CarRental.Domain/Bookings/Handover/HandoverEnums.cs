namespace CarRental.Domain.Bookings.Handover;

/// <summary>When the inspection (état des lieux) is made. Values are stored: never renumber them.</summary>
public enum InspectionKind
{
    /// <summary>At the departure of the rental (the vehicle leaves).</summary>
    Departure = 1,

    /// <summary>At the return of the rental (the vehicle is back).</summary>
    Return = 2,
}

/// <summary>Life of an inspection: a draft filled step by step (resumable), then completed with the hand-over.</summary>
public enum InspectionStatus
{
    /// <summary>Being filled: photos, readings, damages and signatures can still change.</summary>
    Draft = 1,

    /// <summary>Completed with the departure or the return of the rental file; only the return charges can be revised.</summary>
    Completed = 2,
}

/// <summary>
/// What a photo of an inspection shows. The eight guided views are taken at every departure (SPEC §6, "8 photos
/// minimum") and suggested at the return; damage and other photos are additional.
/// </summary>
public enum PhotoSlot
{
    Front = 1,
    Rear = 2,
    Left = 3,
    Right = 4,
    InteriorFront = 5,
    InteriorRear = 6,

    /// <summary>The dashboard, with the mileage and the fuel gauge readable.</summary>
    Dashboard = 7,

    Boot = 8,

    /// <summary>A close-up of a damage (linked to its mark).</summary>
    Damage = 20,

    /// <summary>Any other photo.</summary>
    Other = 30,
}

/// <summary>The guided photo views, in the order of the wizard.</summary>
public static class PhotoSlots
{
    public static IReadOnlyList<PhotoSlot> Guided { get; } =
    [
        PhotoSlot.Front,
        PhotoSlot.Rear,
        PhotoSlot.Left,
        PhotoSlot.Right,
        PhotoSlot.InteriorFront,
        PhotoSlot.InteriorRear,
        PhotoSlot.Dashboard,
        PhotoSlot.Boot,
    ];

    /// <summary>A guided view holds one photo (a new one replaces it: "retake").</summary>
    public static bool IsGuided(this PhotoSlot slot) => slot is >= PhotoSlot.Front and <= PhotoSlot.Boot;
}

/// <summary>A view of the damage schematic of the vehicle.</summary>
public enum DamageView
{
    Front = 1,
    Rear = 2,
    Left = 3,
    Right = 4,
    Top = 5,
    Interior = 6,
}

/// <summary>Type of a damage.</summary>
public enum DamageType
{
    Scratch = 1,
    Dent = 2,

    /// <summary>A crack or a chip (glass, lights, paint).</summary>
    CrackOrChip = 3,

    MissingPart = 4,
    Stain = 5,
    Other = 99,
}

/// <summary>Severity of a damage.</summary>
public enum DamageSeverity
{
    Minor = 1,
    Moderate = 2,
    Major = 3,
}

/// <summary>Who signs an inspection.</summary>
public enum SignatureRole
{
    Customer = 1,
    Agent = 2,
}

/// <summary>
/// A line of the return charges (ADR-0042). They are not quote versions: they add to the balance of the file and feed
/// the final invoice (phase 3).
/// </summary>
public enum ReturnChargeKind
{
    /// <summary>Kilometres beyond the mileage included in the rental.</summary>
    ExtraMileage = 1,

    /// <summary>Missing eighths of fuel (full-to-full: back to the departure level).</summary>
    Fuel = 2,

    /// <summary>Rental days added by a return beyond the tolerance, priced at the current plan.</summary>
    LateReturn = 3,

    /// <summary>The daily contribution (excluding VAT, then VAT) of the late days, kept apart for accounting.</summary>
    LateContribution = 4,

    Cleaning = 5,

    /// <summary>A repair of a new damage; the damages together are capped at the franchise.</summary>
    Damage = 6,

    /// <summary>Negative line bringing the damages down to the franchise (computed, never entered).</summary>
    FranchiseCap = 7,

    /// <summary>Anything else, with a mandatory description and reason.</summary>
    Other = 99,
}

/// <summary>Language of the rental contract document.</summary>
public enum ContractLanguage
{
    French = 1,
    English = 2,
}
