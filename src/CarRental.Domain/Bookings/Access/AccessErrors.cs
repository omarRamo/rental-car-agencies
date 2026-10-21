using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Access;

/// <summary>Errors of the public booking funnel (texts: resources <c>Error.{code}</c>, never an internal check).</summary>
public static class FunnelErrors
{
    public static readonly Error SessionNotFound = Error.NotFound(
        "booking.funnel.session_not_found",
        "The booking link is unknown or has expired: search again.");

    public static readonly Error SessionExpired = Error.Conflict(
        "booking.funnel.session_expired",
        "The selection has expired (the price was guaranteed for 24 hours): search again.");

    public static readonly Error AlreadySubmitted = Error.Conflict(
        "booking.funnel.already_submitted",
        "This request was already sent.");

    public static readonly Error CategoryUnavailable = Error.Conflict(
        "booking.funnel.category_unavailable",
        "This category is no longer available for these dates.");

    public static readonly Error DriversRequired = Error.Validation(
        "booking.funnel.drivers_required",
        "The drivers must be entered before the payment step.");

    public static readonly Error DocumentConsentRequired = Error.Validation(
        "booking.funnel.document_consent_required",
        "The consent to the processing of the identity documents is required.");

    public static readonly Error TermsRequired = Error.Validation(
        "booking.funnel.terms_required",
        "The rental terms must be accepted.");

    public static readonly Error VerificationFailed = Error.Validation(
        "booking.funnel.verification_failed",
        "The anti-robot check failed: try again.");

    public static readonly Error UploadSlotInvalid = Error.Validation(
        "booking.funnel.upload_slot_invalid",
        "Unknown photo slot.");

    public static readonly Error UploadLimitReached = Error.Conflict(
        "booking.funnel.upload_limit",
        "Too many photos were sent for this booking.");

    public static readonly Error DriversNotEligible = Error.Validation(
        "booking.funnel.drivers_not_eligible",
        "A driver does not meet the rental conditions.");
}

/// <summary>Errors of "Ma réservation" (no message ever tells whether a booking exists).</summary>
public static class AccessErrors
{
    /// <summary>A link or a code that is unknown, used, expired, or for no booking: always the same message.</summary>
    public static readonly Error AccessInvalid = Error.Validation(
        "booking.access.invalid",
        "This link or code is not valid (or no longer): ask for a new one.");

    public static readonly Error AccessLocked = Error.Conflict(
        "booking.access.locked",
        "Too many wrong codes: ask for a new link.");

    public static readonly Error SessionRequired = Error.Forbidden(
        "booking.access.session_required",
        "Open your booking from the link or the code received by e-mail.");

    public static readonly Error CancellationNotAllowed = Error.Conflict(
        "booking.access.cancellation_not_allowed",
        "This booking can no longer be cancelled online: contact the agency.");

    public static readonly Error NothingToPay = Error.Conflict(
        "booking.access.nothing_to_pay",
        "Nothing is left to pay online for this booking.");

    public static readonly Error PaymentUnavailable = Error.Failure(
        "booking.access.payment_unavailable",
        "Online payment is not available at the moment: contact the agency.");

    public static readonly Error DocumentsNotExpected = Error.Conflict(
        "booking.access.documents_not_expected",
        "Documents can no longer be sent for this booking: contact the agency.");

    public static readonly Error DocumentNotFound = Error.NotFound(
        "booking.access.document_not_found",
        "This document is not available.");

    public static readonly Error NoContactEmail = Error.Validation(
        "booking.access.no_contact_email",
        "The main driver has no e-mail address: add it on the driver's page first.");
}
