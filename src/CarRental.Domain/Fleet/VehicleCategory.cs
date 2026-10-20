using CarRental.Domain.Common;

namespace CarRental.Domain.Fleet;

/// <summary>
/// A category sold "or similar" (ADR-0005), identified by its ACRISS code within an agency, e.g. <c>ECMR</c> "Peugeot
/// 208 or similar". Transmission and air conditioning follow the code; the fuel must match it when the code states one.
/// The deposit (caution) applies to a rental, the reduced deposit when the franchise waiver is bought.
/// </summary>
public sealed class VehicleCategory : AggregateRoot, ISoftDeletable
{
    public const int NameMaxLength = 80;
    public const int ExampleModelMaxLength = 120;
    public const int MaxSeats = 9;
    public const int MaxDoors = 5;
    public const int MaxBags = 10;
    public const int MaxDisplayOrder = 9999;

    private VehicleCategory()
    {
    }

    private VehicleCategory(Guid id, Guid agencyId, AcrissCode code, VehicleCategoryDetails details)
        : base(id)
    {
        AgencyId = agencyId;
        Apply(code, details);
        IsActive = true;
    }

    public Guid AgencyId { get; private set; }

    public AcrissCode Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    /// <summary>Model shown to customers with "or similar", e.g. <c>Peugeot 208</c>.</summary>
    public string ExampleModel { get; private set; } = null!;

    public int Seats { get; private set; }

    public int Doors { get; private set; }

    public int LargeBags { get; private set; }

    public int SmallBags { get; private set; }

    /// <summary>Derived from the third letter of the ACRISS code.</summary>
    public Transmission Transmission { get; private set; }

    public FuelType Fuel { get; private set; }

    /// <summary>Derived from the fourth letter of the ACRISS code.</summary>
    public bool AirConditioning { get; private set; }

    /// <summary>Deposit (caution) of a rental, in TND.</summary>
    public Money Deposit { get; private set; }

    /// <summary>Deposit when the customer buys the franchise waiver (rachat de franchise).</summary>
    public Money ReducedDeposit { get; private set; }

    /// <summary>Path of the public photo in the fleet container, or <see langword="null"/>.</summary>
    public string? PhotoPath { get; private set; }

    public string? PhotoContentType { get; private set; }

    /// <summary>SHA-256 (hex) of the photo, used as entity tag and URL version.</summary>
    public string? PhotoHash { get; private set; }

    public int DisplayOrder { get; private set; }

    public bool IsActive { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAtUtc { get; private set; }

    public bool HasPhoto => PhotoPath is not null;

    /// <summary>Short version of the photo (first 16 hex digits of its hash), or <see langword="null"/>.</summary>
    public string? PhotoVersion => PhotoHash?[..16];

    public static Result<VehicleCategory> Create(Guid agencyId, VehicleCategoryDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (agencyId == Guid.Empty)
        {
            return FleetErrors.AgencyRequired;
        }

        var validated = Validate(details);
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        var category = new VehicleCategory(NewId(), agencyId, validated.Value, details.Normalize());
        category.Raise(new VehicleCategoryCreated(category.Id, agencyId, category.Code));
        return category;
    }

    public Result Update(VehicleCategoryDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var validated = Validate(details);
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        Apply(validated.Value, details.Normalize());
        Raise(new VehicleCategoryUpdated(Id, Code));
        return Result.Success();
    }

    public void Activate() => SetActive(true);

    public void Deactivate() => SetActive(false);

    public void SetPhoto(StoredPhoto photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        PhotoPath = photo.Path;
        PhotoContentType = photo.ContentType;
        PhotoHash = photo.ContentHash;
    }

    public void RemovePhoto()
    {
        PhotoPath = null;
        PhotoContentType = null;
        PhotoHash = null;
    }

    /// <summary>
    /// The fuel stated by the fourth letter of an ACRISS code (D/Q diesel, H/I hybrid, E/C electric, L/S LPG, V/Z petrol),
    /// or <see langword="null"/> when the code leaves it open (R/N unspecified, or an energy the platform does not model).
    /// </summary>
    public static FuelType? FuelOf(AcrissCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return code.FuelAndAir switch
        {
            'D' or 'Q' => FuelType.Diesel,
            'H' or 'I' => FuelType.Hybrid,
            'E' or 'C' => FuelType.Electric,
            'L' or 'S' => FuelType.Lpg,
            'V' or 'Z' => FuelType.Petrol,
            _ => null,
        };
    }

    private static Result<AcrissCode> Validate(VehicleCategoryDetails details)
    {
        var code = AcrissCode.Create(details.Code);
        if (code.IsFailure)
        {
            return code.Error;
        }

        var normalized = details.Normalize();
        if (normalized.Name.Length is 0 or > NameMaxLength)
        {
            return FleetErrors.CategoryNameInvalid;
        }

        if (normalized.ExampleModel.Length is 0 or > ExampleModelMaxLength)
        {
            return FleetErrors.CategoryExampleModelInvalid;
        }

        if (details.Seats is < 1 or > MaxSeats
            || details.Doors is < 2 or > MaxDoors
            || details.LargeBags is < 0 or > MaxBags
            || details.SmallBags is < 0 or > MaxBags)
        {
            return FleetErrors.CategoryCapacityInvalid;
        }

        if (!Enum.IsDefined(details.Fuel))
        {
            return FleetErrors.CategoryFuelInvalid;
        }

        if (FuelOf(code.Value) is { } stated && stated != details.Fuel)
        {
            return FleetErrors.CategoryFuelMismatch;
        }

        if (!FleetAmounts.IsValid(details.Deposit)
            || !FleetAmounts.IsValid(details.ReducedDeposit)
            || details.ReducedDeposit > details.Deposit)
        {
            return FleetErrors.CategoryDepositInvalid;
        }

        if (details.DisplayOrder is < 0 or > MaxDisplayOrder)
        {
            return FleetErrors.CategoryDisplayOrderInvalid;
        }

        return code.Value;
    }

    private void Apply(AcrissCode code, VehicleCategoryDetails details)
    {
        Code = code;
        Name = details.Name;
        ExampleModel = details.ExampleModel;
        Seats = details.Seats;
        Doors = details.Doors;
        LargeBags = details.LargeBags;
        SmallBags = details.SmallBags;
        Transmission = code.IsAutomatic ? Transmission.Automatic : Transmission.Manual;
        Fuel = details.Fuel;
        AirConditioning = code.HasAirConditioning;
        Deposit = details.Deposit;
        ReducedDeposit = details.ReducedDeposit;
        DisplayOrder = details.DisplayOrder;
    }

    private void SetActive(bool active)
    {
        if (IsActive == active)
        {
            return;
        }

        IsActive = active;
        Raise(new VehicleCategoryActivationChanged(Id, active));
    }
}

/// <summary>Editable details of a category. <paramref name="Code"/> is an ACRISS code (validated, case-insensitive).</summary>
public sealed record VehicleCategoryDetails(
    string Code,
    string Name,
    string ExampleModel,
    int Seats,
    int Doors,
    int LargeBags,
    int SmallBags,
    FuelType Fuel,
    Money Deposit,
    Money ReducedDeposit,
    int DisplayOrder = 0)
{
    internal VehicleCategoryDetails Normalize() => this with
    {
        Name = Name?.Trim() ?? string.Empty,
        ExampleModel = ExampleModel?.Trim() ?? string.Empty,
    };
}

/// <summary>Amounts accepted by the fleet: non-negative Tunisian dinars.</summary>
public static class FleetAmounts
{
    public static bool IsValid(Money amount) => amount.Currency == Currency.Tnd && !amount.IsNegative;

    public static bool IsValid(Money? amount) => amount is not { } value || IsValid(value);
}
