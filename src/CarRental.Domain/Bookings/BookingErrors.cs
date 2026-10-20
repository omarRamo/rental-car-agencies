using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings;

/// <summary>
/// Business errors of the Bookings module (codes <c>booking.*</c>, stable; texts in <c>SharedResources</c> as
/// <c>Error.{code}</c>). Errors of the engines a booking relies on keep their own codes (<c>availability.*</c>,
/// <c>pricing.*</c>, <c>customers.*</c>).
/// </summary>
public static class BookingErrors
{
    // Cross-cutting
    public static readonly Error Forbidden = Error.Forbidden("booking.forbidden", "The current user lacks the required booking permission.");

    public static readonly Error NotFound = Error.NotFound("booking.not_found", "The rental file does not exist.");

    /// <summary>The command is not allowed from the current status (docs/conception §2).</summary>
    public static readonly Error InvalidTransition = Error.Conflict(
        "booking.invalid_transition",
        "This action is not allowed in the current status of the rental file.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "booking.concurrent_update",
        "The rental file was changed by someone else in the meantime; reload and try again.");

    public static readonly Error ReasonRequired = Error.Validation("booking.reason_required", "A reason is required for this action.");

    public static readonly Error ReasonTooLong = Error.Validation(
        "booking.reason_too_long",
        $"A reason has {Booking.ReasonMaxLength} characters at most.");

    // Details
    public static readonly Error AgencyRequired = Error.Validation("booking.agency_required", "An agency is required.");

    public static readonly Error CategoryRequired = Error.Validation("booking.category_required", "A vehicle category is required.");

    public static readonly Error LocationRequired = Error.Validation("booking.location_required", "The pick-up and return locations are required.");

    public static readonly Error PeriodInvalid = Error.Validation("booking.period_invalid", "The return must be after the pick-up.");

    public static readonly Error PickupInPast = Error.Validation("booking.pickup_in_past", "The pick-up cannot be in the past.");

    public static readonly Error MainDriverRequired = Error.Validation("booking.main_driver_required", "A main driver is required.");

    public static readonly Error PayerRequired = Error.Validation("booking.payer_required", "A payer is required.");

    public static readonly Error DriversIdentical = Error.Validation(
        "booking.drivers_identical",
        "The second driver must be another person than the main driver.");

    public static readonly Error ChannelInvalid = Error.Validation("booking.channel_invalid", "This sales channel cannot be used for this kind of booking.");

    public static readonly Error ChannelReferenceInvalid = Error.Validation(
        "booking.channel_reference_invalid",
        $"The channel reference has {Booking.ChannelReferenceMaxLength} characters at most.");

    public static readonly Error ChannelReferenceTaken = Error.Conflict(
        "booking.channel_reference_taken",
        "Another rental file already carries this reference of the channel.");

    public static readonly Error AttributionInvalid = Error.Validation(
        "booking.attribution_invalid",
        $"Source and campaign fields have {BookingAttribution.FieldMaxLength} characters at most.");

    public static readonly Error ExtrasInvalid = Error.Validation(
        "booking.extras_invalid",
        "An extra has an invalid code or quantity (1 to 10).");

    public static readonly Error PromoCodeInvalid = Error.Validation(
        "booking.promo_code_invalid",
        $"A promotion code has {Booking.PromoCodeMaxLength} characters at most.");

    public static readonly Error OptionExpiryInvalid = Error.Validation(
        "booking.option_expiry_invalid",
        "The option must expire in the future.");

    public static readonly Error NotesTooLong = Error.Validation(
        "booking.notes_too_long",
        $"Notes have {Booking.NotesMaxLength} characters at most.");

    // Workflow
    public static readonly Error HoldExpired = Error.Conflict("booking.hold_expired", "The hold of the request has expired.");

    public static readonly Error OptionExpired = Error.Conflict("booking.option_expired", "The option has expired.");

    public static readonly Error DeadlineNotReached = Error.Conflict(
        "booking.deadline_not_reached",
        "The hold, option or payment deadline is not reached yet.");

    public static readonly Error NoShowTooEarly = Error.Conflict(
        "booking.no_show_too_early",
        "A no-show can only be recorded once the no-show delay after the planned pick-up has passed.");

    public static readonly Error InformationReasonInvalid = Error.Validation(
        "booking.information_reason_invalid",
        "Choose a predefined reason for the information request.");

    public static readonly Error PaymentDueInvalid = Error.Validation("booking.payment_due_invalid", "The payment deadline must be in the future.");

    /// <summary>A driver is not eligible (age, licence seniority, documents): the issues are shown by the checklist.</summary>
    public static readonly Error DriversNotEligible = Error.Conflict(
        "booking.drivers_not_eligible",
        "A driver does not meet the eligibility rules (age, licence, documents valid until the return).");

    /// <summary>Generic outcome of the internal customer check; never reveals the reason (SPEC §5, blacklist).</summary>
    public static readonly Error CustomerCheckFailed = Error.Conflict(
        "booking.customer_check_failed",
        "The customer check does not allow this rental file.");

    public static readonly Error CancellationFeeInvalid = Error.Validation(
        "booking.cancellation_fee_invalid",
        "A cancellation fee is a non-negative amount in TND.");

    public static readonly Error ActualPickupInvalid = Error.Validation(
        "booking.actual_pickup_invalid",
        "The actual pick-up cannot be in the future.");

    public static readonly Error ActualReturnInvalid = Error.Validation(
        "booking.actual_return_invalid",
        "The actual return must be after the actual pick-up and not in the future.");

    // Vehicles
    public static readonly Error VehicleRequired = Error.Conflict("booking.vehicle.required", "Assign a vehicle before the departure.");

    public static readonly Error VehicleNotAssigned = Error.Conflict("booking.vehicle.not_assigned", "No vehicle is assigned to the rental file.");

    public static readonly Error VehicleAlreadyAssigned = Error.Conflict("booking.vehicle.already_assigned", "This vehicle is already assigned to the rental file.");

    public static readonly Error VehicleGuaranteedModel = Error.Conflict(
        "booking.vehicle.guaranteed_model",
        "The rental file books this exact vehicle (guaranteed model): amend the booking to change it.");

    public static readonly Error VehicleRequiredForWaiver = Error.Validation(
        "booking.vehicle.waiver_without_vehicle",
        "A compliance waiver applies to an assigned vehicle.");

    // Amendments
    public static readonly Error AmendmentNoChange = Error.Validation("booking.amendment.no_change", "Nothing was changed.");

    public static readonly Error AmendmentExtensionOnly = Error.Conflict(
        "booking.amendment.extension_only",
        "A rental in progress can only be extended: a later return, everything else unchanged.");

    public static readonly Error DiscountInvalid = Error.Validation(
        "booking.amendment.discount_invalid",
        "A manual discount is a positive amount in TND, at most the total of the quote.");

    public static readonly Error NegativeTotal = Error.Conflict("booking.amendment.negative_total", "The quote total cannot become negative.");

    // Quotes
    public static readonly Error QuoteInvalid = Error.Validation(
        "booking.quote.invalid",
        "The quote is inconsistent: its lines must add up to its totals.");

    public static readonly Error QuoteNotFound = Error.NotFound("booking.quote.not_found", "The quote does not exist.");

    public static readonly Error QuoteExpired = Error.Conflict("booking.quote.expired", "The locked quote has expired: ask for a new price.");

    public static readonly Error QuoteCodeInvalid = Error.Validation("booking.quote.code_invalid", "A quote code has six letters or digits.");

    public static readonly Error QuoteCodeUnavailable = Error.Failure("booking.quote.code_unavailable", "No free quote code could be generated; try again.");

    public static readonly Error QuotePriceChanged = Error.Conflict(
        "booking.quote.price_changed",
        "The price changed since it was shown: check the new price and confirm again.");
}
