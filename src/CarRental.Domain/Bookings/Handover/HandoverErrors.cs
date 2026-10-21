using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Handover;

/// <summary>
/// Business errors of the departure and return (codes <c>handover.*</c>, stable; texts in <c>SharedResources</c> as
/// <c>Error.{code}</c>). The transitions of the rental file keep their <c>booking.*</c> codes.
/// </summary>
public static class HandoverErrors
{
    // Inspection
    public static readonly Error InspectionNotFound = Error.NotFound("handover.inspection.not_found", "The inspection does not exist.");

    public static readonly Error InspectionCompleted = Error.Conflict(
        "handover.inspection.completed",
        "The inspection is completed: it can no longer be changed.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "handover.inspection.concurrent_update",
        "The inspection was changed by someone else in the meantime; reload and try again.");

    public static readonly Error InspectionKindMismatch = Error.Validation(
        "handover.inspection.kind_mismatch",
        "This step does not belong to this kind of inspection.");

    public static readonly Error VehicleRequired = Error.Validation(
        "handover.inspection.vehicle_required",
        "A vehicle must be assigned to the rental file and confirmed first.");

    public static readonly Error VehicleChanged = Error.Conflict(
        "handover.inspection.vehicle_changed",
        "The vehicle of the rental file changed since the inspection started: confirm the new vehicle.");

    public static readonly Error MileageInvalid = Error.Validation(
        "handover.inspection.mileage_invalid",
        $"The mileage is between 0 and {Inspection.MaxMileage} km.");

    public static readonly Error MileageBelowReference = Error.Validation(
        "handover.inspection.mileage_below_reference",
        "The mileage cannot be below the last known mileage of the vehicle (or the mileage at the departure).");

    public static readonly Error FuelInvalid = Error.Validation(
        "handover.inspection.fuel_invalid",
        $"The fuel level is given in eighths, from 0 to {Inspection.FullTank}.");

    public static readonly Error AccessoriesInvalid = Error.Validation(
        "handover.inspection.accessories_invalid",
        $"Keys are counted from 0 to {AccessoryChecklist.MaxKeys}, child seats from 0 to {AccessoryChecklist.MaxChildSeats}.");

    public static readonly Error ReadingsMissing = Error.Validation(
        "handover.inspection.readings_missing",
        "The mileage, the fuel level and the accessories are required.");

    public static readonly Error OriginalsIncomplete = Error.Validation(
        "handover.inspection.originals_incomplete",
        "The original identity document and driving licence of every driver must have been seen.");

    public static readonly Error OriginalsUnknownDriver = Error.Validation(
        "handover.inspection.originals_unknown_driver",
        "The originals were checked for a person who is not a driver of the rental file.");

    public static readonly Error PhotosMissing = Error.Validation(
        "handover.inspection.photos_missing",
        $"The {PhotoSlots.Guided.Count} guided photos are required at the departure.");

    public static readonly Error TooManyPhotos = Error.Validation(
        "handover.inspection.too_many_photos",
        $"An inspection has {Inspection.MaxPhotos} photos at most.");

    public static readonly Error PhotoNotFound = Error.NotFound("handover.inspection.photo_not_found", "The photo does not exist.");

    public static readonly Error PhotoSlotInvalid = Error.Validation("handover.inspection.photo_slot_invalid", "The view of the photo is not known.");

    public static readonly Error DamageNotFound = Error.NotFound("handover.inspection.damage_not_found", "The damage does not exist.");

    public static readonly Error DamageInvalid = Error.Validation(
        "handover.inspection.damage_invalid",
        "A damage needs a known view, type and severity, and a position between 0 and 100 % on the schematic.");

    public static readonly Error DamageNoteTooLong = Error.Validation(
        "handover.inspection.damage_note_too_long",
        $"A damage note has {DamageMark.NoteMaxLength} characters at most.");

    public static readonly Error TooManyDamages = Error.Validation(
        "handover.inspection.too_many_damages",
        $"An inspection has {Inspection.MaxDamages} damages at most.");

    public static readonly Error SignaturesMissing = Error.Validation(
        "handover.inspection.signatures_missing",
        "The customer and the agent must sign, or the signature on paper must be recorded with its reason.");

    public static readonly Error SignerNameRequired = Error.Validation(
        "handover.inspection.signer_name_required",
        $"The name of the signer is required ({InspectionSignature.SignerNameMaxLength} characters at most).");

    public static readonly Error PaperReasonRequired = Error.Validation(
        "handover.inspection.paper_reason_required",
        $"Signing on paper needs a reason ({Inspection.ReasonMinLength} to {Inspection.ReasonMaxLength} characters).");

    public static readonly Error NotesTooLong = Error.Validation(
        "handover.inspection.notes_too_long",
        $"The notes have {Inspection.NotesMaxLength} characters at most.");

    // Files (photos and signatures)
    public static readonly Error ImageEmpty = Error.Validation("handover.image.empty", "The file is empty.");

    public static readonly Error ImageTooLarge = Error.Validation(
        "handover.image.too_large",
        "The file is too large: 10 MB at most for a photo, 1 MB for a thumbnail or a signature.");

    public static readonly Error ImageUnsupported = Error.Validation(
        "handover.image.unsupported",
        "The file is not a JPEG, PNG or WebP image (a signature is a PNG image).");

    // Departure
    public static readonly Error BalanceDue = Error.Conflict(
        "handover.departure.balance_due",
        "The amount due must be paid before the departure, unless a manager deferred it.");

    public static readonly Error DepositMissing = Error.Conflict(
        "handover.departure.deposit_missing",
        "The deposit must be held before the departure, unless a manager waived it.");

    public static readonly Error ReasonRequired = Error.Validation(
        "handover.reason_required",
        $"A reason is required ({Inspection.ReasonMinLength} to {Inspection.ReasonMaxLength} characters).");

    public static readonly Error NothingToDefer = Error.Validation(
        "handover.departure.nothing_to_defer",
        "Choose to defer the balance, to waive the deposit, or both.");

    // Return charges
    public static readonly Error ChargeInvalid = Error.Validation(
        "handover.charges.invalid",
        "A charge has a known kind, a quantity from 0 and a unit price from 0, in millimes.");

    public static readonly Error ChargeReasonRequired = Error.Validation(
        "handover.charges.reason_required",
        "A reason is required to change or waive a proposed charge, and for any other charge.");

    public static readonly Error ChargeDescriptionRequired = Error.Validation(
        "handover.charges.description_required",
        $"A damage or another charge needs a description ({ReturnCharge.DescriptionMaxLength} characters at most).");

    public static readonly Error ChargeDamageInvalid = Error.Validation(
        "handover.charges.damage_invalid",
        "A damage charge refers to a new damage of this return.");

    public static readonly Error ChargeNotEditable = Error.Validation(
        "handover.charges.not_editable",
        "This charge is computed by the platform: it cannot be entered.");

    public static readonly Error TooManyCharges = Error.Validation(
        "handover.charges.too_many",
        $"A return has {ReturnCharges.MaxLines} charges at most.");

    public static readonly Error ChargesProposalChanged = Error.Conflict(
        "handover.charges.proposal_changed",
        "The proposed charges changed meanwhile (time, prices or readings): review them again.");

    public static readonly Error ChargesNotRevisable = Error.Conflict(
        "handover.charges.not_revisable",
        "The return charges can only be revised while the rental file is returned and not closed.");

    // Closing
    public static readonly Error CloseBalanceDue = Error.Conflict(
        "handover.close.balance_due",
        "The balance of the rental file must be zero to close it (collect it or pay it back).");

    public static readonly Error CloseDepositHeld = Error.Conflict(
        "handover.close.deposit_held",
        "A deposit is still held: release or retain it before closing the rental file.");

    // Contract
    public static readonly Error ContractNotFound = Error.NotFound("handover.contract.not_found", "The rental contract does not exist.");

    public static readonly Error ContractLanguageInvalid = Error.Validation(
        "handover.contract.language_invalid",
        "The contract is written in French or in English.");
}
