using CarRental.Domain.Common;

namespace CarRental.Domain.Cash;

/// <summary>Business errors of the Cash module (codes <c>cash.*</c>; texts in <c>SharedResources</c> as <c>Error.{code}</c>).</summary>
public static class CashErrors
{
    // Cross-cutting
    public static readonly Error Forbidden = Error.Forbidden(
        "cash.forbidden",
        "The current user lacks the permission to manage this cash session.");

    public static readonly Error AgencyRequired = Error.Validation("cash.agency_required", "An agency is required.");

    public static readonly Error AgencyNotFound = Error.NotFound("cash.agency_not_found", "The agency does not exist or is inactive.");

    public static readonly Error CashierRequired = Error.Validation(
        "cash.cashier_required",
        "A cash session belongs to a signed-in user.");

    public static readonly Error AmountInvalid = Error.Validation(
        "cash.amount_invalid",
        "A cash amount is positive, in Tunisian dinars with millimes at most, and at most 1 000 000 TND.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "cash.concurrent_update",
        "The cash session was changed by someone else in the meantime; reload and try again.");

    // Sessions
    public static readonly Error OpeningFloatInvalid = Error.Validation(
        "cash.session.opening_float_invalid",
        "The opening float is zero or positive, in Tunisian dinars with millimes at most.");

    public static readonly Error SessionNotFound = Error.NotFound("cash.session.not_found", "The cash session does not exist.");

    public static readonly Error SessionNotOwned = Error.Forbidden(
        "cash.session.not_owned",
        "This cash session belongs to another user.");

    public static readonly Error NoOpenSession = Error.Conflict(
        "cash.session.none_open",
        "Cash is recorded in the open cash session of the agent: open your cash session first.");

    public static readonly Error SessionAlreadyOpen = Error.Conflict(
        "cash.session.already_open",
        "You already have an open cash session: close it before opening another one.");

    public static readonly Error SessionAlreadyExistsForDay = Error.Conflict(
        "cash.session.already_exists_for_day",
        "You already had a cash session today in this agency: a closed session is not reopened.");

    public static readonly Error SessionAgencyMismatch = Error.Conflict(
        "cash.session.agency_mismatch",
        "Your open cash session belongs to another agency.");

    public static readonly Error SessionClosed = Error.Conflict(
        "cash.session.closed",
        "The cash session is closed: no more movement can be recorded in it.");

    public static readonly Error SessionLocked = Error.Conflict(
        "cash.session.locked",
        "The cash session is locked: it can no longer change; record a correction in a later session.");

    public static readonly Error SessionNotClosed = Error.Conflict(
        "cash.session.not_closed",
        "Only a closed cash session can be locked.");

    public static readonly Error CountedAmountInvalid = Error.Validation(
        "cash.session.counted_amount_invalid",
        "A counted amount is zero or positive, in Tunisian dinars with millimes at most.");

    public static readonly Error VarianceJustificationRequired = Error.Validation(
        "cash.session.variance_justification_required",
        $"A difference between the counted and the expected cash needs a justification of {CashSession.JustificationMinLength} characters at least.");

    public static readonly Error JustificationTooLong = Error.Validation(
        "cash.session.justification_too_long",
        $"A justification has {CashSession.JustificationMaxLength} characters at most.");

    // Movements
    public static readonly Error ReasonRequired = Error.Validation(
        "cash.movement.reason_required",
        $"A manual movement or an adjustment needs a reason of {CashMovement.ReasonMinLength} characters at least.");

    public static readonly Error ReasonTooLong = Error.Validation(
        "cash.movement.reason_too_long",
        $"A reason has {CashMovement.ReasonMaxLength} characters at most.");

    public static readonly Error DirectionInvalid = Error.Validation("cash.movement.direction_invalid", "A manual movement goes in or out.");

    public static readonly Error PocketInvalid = Error.Validation("cash.movement.pocket_invalid", "Unknown cash pocket.");

    public static readonly Error DuplicateMovement = Error.Conflict(
        "cash.movement.duplicate",
        "This payment or deposit is already recorded in the cash session.");

    public static readonly Error InsufficientCash = Error.Conflict(
        "cash.movement.insufficient_cash",
        "The expected cash of the session does not cover this outflow.");

    public static readonly Error CorrectedSessionInvalid = Error.Validation(
        "cash.adjustment.corrected_session_invalid",
        "The corrected session must be another closed or locked cash session.");
}
