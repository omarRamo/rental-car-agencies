using CarRental.Domain.Common;

namespace CarRental.Domain.Fleet;

/// <summary>Business errors of the Fleet module (codes <c>fleet.*</c>; texts in <c>SharedResources</c> as <c>Error.{code}</c>).</summary>
public static class FleetErrors
{
    // Cross-cutting
    public static readonly Error Forbidden = Error.Forbidden("fleet.forbidden", "The current user lacks the required fleet permission.");

    public static readonly Error AgencyRequired = Error.Validation("fleet.agency_required", "An agency is required.");

    public static readonly Error AmountInvalid = Error.Validation(
        "fleet.amount_invalid",
        "An amount must be a non-negative amount in Tunisian dinars (millimes at most).");

    public static readonly Error ReasonRequired = Error.Validation("fleet.reason_required", "A reason is required for this change.");

    public static readonly Error ReasonTooLong = Error.Validation(
        "fleet.reason_too_long",
        $"A reason has {Vehicle.ReasonMaxLength} characters at most.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "fleet.concurrent_update",
        "The record was changed by someone else in the meantime; reload and try again.");

    // Categories
    public static readonly Error CategoryNotFound = Error.NotFound("fleet.category.not_found", "The vehicle category does not exist.");

    public static readonly Error CategoryNameInvalid = Error.Validation(
        "fleet.category.name_invalid",
        $"The category name is required ({VehicleCategory.NameMaxLength} characters max).");

    public static readonly Error CategoryExampleModelInvalid = Error.Validation(
        "fleet.category.example_model_invalid",
        $"The example model is required ({VehicleCategory.ExampleModelMaxLength} characters max).");

    public static readonly Error CategoryCapacityInvalid = Error.Validation(
        "fleet.category.capacity_invalid",
        $"Seats (1-{VehicleCategory.MaxSeats}), doors (2-{VehicleCategory.MaxDoors}) and bags (0-{VehicleCategory.MaxBags}) are out of range.");

    public static readonly Error CategoryFuelInvalid = Error.Validation("fleet.category.fuel_invalid", "Unknown fuel type.");

    public static readonly Error CategoryFuelMismatch = Error.Validation(
        "fleet.category.fuel_mismatch",
        "The fuel does not match the fuel letter of the ACRISS code.");

    public static readonly Error CategoryDepositInvalid = Error.Validation(
        "fleet.category.deposit_invalid",
        "Deposits are non-negative amounts in TND and the reduced deposit cannot exceed the deposit.");

    public static readonly Error CategoryDisplayOrderInvalid = Error.Validation(
        "fleet.category.display_order_invalid",
        $"The display order is between 0 and {VehicleCategory.MaxDisplayOrder}.");

    public static readonly Error CategoryCodeTaken = Error.Conflict(
        "fleet.category.code_taken",
        "Another category of the agency already uses this ACRISS code.");

    public static readonly Error CategoryHasVehicles = Error.Conflict(
        "fleet.category.has_vehicles",
        "A category with vehicles cannot be deleted; deactivate it instead.");

    public static readonly Error CategoryAgencyMismatch = Error.Validation(
        "fleet.category.agency_mismatch",
        "The category belongs to another agency.");

    // Vehicles
    public static readonly Error VehicleNotFound = Error.NotFound("fleet.vehicle.not_found", "The vehicle does not exist.");

    public static readonly Error PlateNumberInvalid = Error.Validation(
        "fleet.vehicle.plate_invalid",
        $"A plate number has {Vehicle.PlateNumberMinLength} to {Vehicle.PlateNumberMaxLength} characters: letters, digits, spaces or '-'.");

    public static readonly Error PlateNumberTaken = Error.Conflict(
        "fleet.vehicle.plate_taken",
        "Another vehicle of the agency already has this plate number.");

    public static readonly Error MakeInvalid = Error.Validation(
        "fleet.vehicle.make_invalid",
        $"The make is required ({Vehicle.MakeMaxLength} characters max).");

    public static readonly Error ModelInvalid = Error.Validation(
        "fleet.vehicle.model_invalid",
        $"The model is required ({Vehicle.ModelMaxLength} characters max).");

    public static readonly Error YearInvalid = Error.Validation(
        "fleet.vehicle.year_invalid",
        $"The year is between {Vehicle.MinYear} and {Vehicle.MaxYear}.");

    public static readonly Error ColorInvalid = Error.Validation(
        "fleet.vehicle.color_invalid",
        $"The color has {Vehicle.ColorMaxLength} characters at most.");

    public static readonly Error VehicleCapacityInvalid = Error.Validation(
        "fleet.vehicle.capacity_invalid",
        $"Seats (1-{Vehicle.MaxSeats}) and bags (0-{Vehicle.MaxBags}) are out of range.");

    public static readonly Error TransmissionInvalid = Error.Validation("fleet.vehicle.transmission_invalid", "Unknown transmission.");

    public static readonly Error FuelInvalid = Error.Validation("fleet.vehicle.fuel_invalid", "Unknown fuel type.");

    public static readonly Error InServiceDateInvalid = Error.Validation(
        "fleet.vehicle.in_service_date_invalid",
        "The date put in service cannot precede the first registration date.");

    public static readonly Error MileageInvalid = Error.Validation(
        "fleet.vehicle.mileage_invalid",
        $"The mileage is between 0 and {Vehicle.MaxMileage} km.");

    public static readonly Error MileageDecrease = Error.Validation(
        "fleet.vehicle.mileage_decrease",
        "The mileage never decreases; use a correction with a reason.");

    public static readonly Error MileageUnchanged = Error.Validation("fleet.vehicle.mileage_unchanged", "The corrected mileage is the current one.");

    public static readonly Error VehicleSold = Error.Conflict("fleet.vehicle.sold", "The vehicle is sold: it can no longer be changed.");

    public static readonly Error InvalidStatusTransition = Error.Conflict(
        "fleet.vehicle.invalid_status_transition",
        "This status change is not allowed.");

    public static readonly Error MaintenanceInProgress = Error.Conflict(
        "fleet.vehicle.maintenance_in_progress",
        "A maintenance is in progress: complete it first.");

    public static readonly Error SameCategory = Error.Validation("fleet.vehicle.same_category", "The vehicle is already in this category.");

    public static readonly Error SaleDateInvalid = Error.Validation(
        "fleet.vehicle.sale_date_invalid",
        "The sale date cannot be in the future nor before the vehicle was put in service.");

    public static readonly Error BuyerTooLong = Error.Validation(
        "fleet.vehicle.buyer_too_long",
        $"The buyer has {Vehicle.BuyerMaxLength} characters at most.");

    public static readonly Error TooManyPhotos = Error.Conflict(
        "fleet.vehicle.too_many_photos",
        $"A vehicle has {Vehicle.MaxPhotos} photos at most.");

    // Documents
    public static readonly Error DocumentNotFound = Error.NotFound("fleet.document.not_found", "The document does not exist.");

    public static readonly Error DocumentTypeInvalid = Error.Validation("fleet.document.type_invalid", "Unknown document type.");

    public static readonly Error DocumentNumberInvalid = Error.Validation(
        "fleet.document.number_invalid",
        $"The document number is required ({VehicleDocument.NumberMaxLength} characters max).");

    public static readonly Error DocumentExpiryRequired = Error.Validation(
        "fleet.document.expiry_required",
        "Insurance, road tax and technical inspection need an expiry date.");

    public static readonly Error DocumentDatesInvalid = Error.Validation(
        "fleet.document.dates_invalid",
        "The expiry date cannot precede the issue date.");

    public static readonly Error DocumentTextTooLong = Error.Validation(
        "fleet.document.text_too_long",
        $"The provider has {VehicleDocument.ProviderMaxLength} characters and the notes {VehicleDocument.NotesMaxLength} characters at most.");

    public static readonly Error DocumentNotCurrent = Error.Conflict(
        "fleet.document.not_current",
        "Only the current document of a type can be corrected.");

    // Maintenance
    public static readonly Error MaintenanceNotFound = Error.NotFound("fleet.maintenance.not_found", "The maintenance does not exist.");

    public static readonly Error MaintenanceTypeInvalid = Error.Validation("fleet.maintenance.type_invalid", "Unknown maintenance type.");

    public static readonly Error MaintenanceTextTooLong = Error.Validation(
        "fleet.maintenance.text_too_long",
        $"The description has {MaintenanceEvent.DescriptionMaxLength} characters and the provider {MaintenanceEvent.ProviderMaxLength} characters at most.");

    public static readonly Error MaintenanceBlockInvalid = Error.Validation(
        "fleet.maintenance.block_invalid",
        "A maintenance block ends after it starts and lasts one year at most.");

    public static readonly Error MaintenanceOverlap = Error.Conflict(
        "fleet.maintenance.overlap",
        "The maintenance overlaps another maintenance of the vehicle.");

    public static readonly Error MaintenanceInvalidTransition = Error.Conflict(
        "fleet.maintenance.invalid_transition",
        "This maintenance cannot be changed in its current state.");

    public static readonly Error MaintenanceNotStarted = Error.Validation(
        "fleet.maintenance.not_started",
        "A maintenance completes after it starts and not in the future.");

    public static readonly Error MaintenanceEndInPast = Error.Validation(
        "fleet.maintenance.end_in_past",
        "The expected end of the maintenance must be in the future.");

    public static readonly Error MaintenanceAlreadyInProgress = Error.Conflict(
        "fleet.maintenance.already_in_progress",
        "Another maintenance of the vehicle is already in progress.");

    public static readonly Error MaintenancePlanInvalid = Error.Validation(
        "fleet.maintenance.plan_invalid",
        $"A maintenance plan has an interval in km (100-{MaintenancePlan.MaxIntervalKm}) and/or in months (1-{MaintenancePlan.MaxIntervalMonths}).");

    public static readonly Error MaintenancePlanNotFound = Error.NotFound(
        "fleet.maintenance.plan_not_found",
        "The maintenance plan does not exist.");

    // Economics and costs
    public static readonly Error PurchasePriceInvalid = Error.Validation(
        "fleet.economics.purchase_price_invalid",
        "The purchase price is a positive amount in TND.");

    public static readonly Error ResidualValueInvalid = Error.Validation(
        "fleet.economics.residual_value_invalid",
        "The residual value is between zero and the purchase price.");

    public static readonly Error DepreciationYearsInvalid = Error.Validation(
        "fleet.economics.depreciation_years_invalid",
        $"The depreciation lasts 1 to {VehicleEconomics.MaxDepreciationYears} years.");

    public static readonly Error FinancingInvalid = Error.Validation(
        "fleet.economics.financing_invalid",
        $"A credit or a leasing has a positive monthly payment and a term of 1 to {VehicleEconomics.MaxFinancingMonths} months; a cash purchase has neither.");

    public static readonly Error CostLabelInvalid = Error.Validation(
        "fleet.cost.label_invalid",
        $"A cost has a label ({VehicleCost.LabelMaxLength} characters max) and a positive amount.");

    public static readonly Error CostNotFound = Error.NotFound("fleet.cost.not_found", "The cost does not exist.");

    // Photos
    public static readonly Error PhotoNotFound = Error.NotFound("fleet.photo.not_found", "The photo does not exist.");

    public static readonly Error PhotoEmpty = Error.Validation("fleet.photo.empty", "The file is empty.");

    public static readonly Error PhotoTooLarge = Error.Validation(
        "fleet.photo.too_large",
        $"A photo is {StoredPhoto.MaxLength / (1024 * 1024)} MB at most.");

    public static readonly Error PhotoUnsupportedFormat = Error.Validation(
        "fleet.photo.unsupported_format",
        "Only JPEG, PNG and WebP photos are accepted.");
}
