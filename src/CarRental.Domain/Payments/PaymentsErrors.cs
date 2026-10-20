using CarRental.Domain.Common;

namespace CarRental.Domain.Payments;

/// <summary>Business errors of the Payments module (codes <c>payments.*</c>; texts in <c>SharedResources</c> as <c>Error.{code}</c>).</summary>
public static class PaymentsErrors
{
    // Cross-cutting
    public static readonly Error Forbidden = Error.Forbidden(
        "payments.forbidden",
        "The current user lacks the permission to read or record payments and deposits.");

    public static readonly Error AgencyRequired = Error.Validation("payments.agency_required", "An agency is required.");

    public static readonly Error BookingRequired = Error.Validation("payments.booking_required", "A booking is required.");

    public static readonly Error AmountInvalid = Error.Validation(
        "payments.amount_invalid",
        "An amount is positive, in Tunisian dinars with millimes at most, and at most 1 000 000 TND.");

    public static readonly Error ReferenceTooLong = Error.Validation(
        "payments.reference_too_long",
        $"An external reference has {Payment.ReferenceMaxLength} characters at most.");

    public static readonly Error NotesTooLong = Error.Validation(
        "payments.notes_too_long",
        $"Notes have {Payment.NotesMaxLength} characters at most.");

    public static readonly Error ReasonRequired = Error.Validation(
        "payments.reason_required",
        $"A reason of {Payment.ReasonMinLength} characters at least is required.");

    public static readonly Error ReasonTooLong = Error.Validation(
        "payments.reason_too_long",
        $"A reason has {Payment.ReasonMaxLength} characters at most.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "payments.concurrent_update",
        "The record was changed by someone else in the meantime; reload and try again.");

    // Payments
    public static readonly Error MethodInvalid = Error.Validation("payments.method_invalid", "Unknown means of payment.");

    public static readonly Error MethodNotCounter = Error.Validation(
        "payments.method_not_counter",
        "Clic to Pay payments come from the online payment flow; they cannot be recorded at the counter.");

    public static readonly Error ReferenceRequired = Error.Validation(
        "payments.reference_required",
        "A cheque number, a transfer reference or a channel booking reference is required for this means of payment.");

    public static readonly Error ConfirmationNotApplicable = Error.Validation(
        "payments.confirmation_not_applicable",
        "Only a bank transfer or a cheque can be recorded as awaiting confirmation.");

    public static readonly Error CashSessionRequired = Error.Validation(
        "payments.cash_session_required",
        "Cash is always recorded in the open cash session of the agent.");

    public static readonly Error PaymentNotFound = Error.NotFound("payments.payment.not_found", "The payment does not exist.");

    public static readonly Error PaymentNotPending = Error.Conflict(
        "payments.payment.not_pending",
        "Only a pending payment can be confirmed or cancelled.");

    public static readonly Error OnlineStatusFromGatewayOnly = Error.Conflict(
        "payments.payment.online_status_from_gateway_only",
        "The status of a Clic to Pay payment only changes from a status confirmed by the payment gateway.");

    public static readonly Error PaymentNotOnline = Error.Conflict(
        "payments.payment.not_online",
        "A payment gateway status only applies to a Clic to Pay payment.");

    public static readonly Error GatewayTransitionInvalid = Error.Conflict(
        "payments.payment.gateway_transition_invalid",
        "The status confirmed by the payment gateway does not follow the current status of the payment.");

    public static readonly Error GatewayAmountInvalid = Error.Conflict(
        "payments.payment.gateway_amount_invalid",
        "The amount confirmed by the payment gateway is missing or exceeds the amount of the payment.");

    // Refunds
    public static readonly Error RefundNotAllowed = Error.Conflict(
        "payments.refund.not_allowed",
        "Only an incoming payment can be refunded.");

    public static readonly Error RefundNotCaptured = Error.Conflict(
        "payments.refund.not_captured",
        "Only a captured payment can be refunded.");

    public static readonly Error RefundExceedsCaptured = Error.Conflict(
        "payments.refund.exceeds_captured",
        "The refunds of a payment cannot exceed what was captured.");

    public static readonly Error RefundMethodInvalid = Error.Validation(
        "payments.refund.method_invalid",
        "Money is paid back at the counter in cash, on the card terminal, by cheque or by bank transfer.");

    public static readonly Error RefundReasonRequired = Error.Validation(
        "payments.refund.reason_required",
        $"The reason of the refund is required ({Payment.ReasonMinLength} to {Payment.ReasonMaxLength} characters).");

    // Deposits
    public static readonly Error DepositNotFound = Error.NotFound("payments.deposit.not_found", "The deposit does not exist.");

    public static readonly Error DepositModeInvalid = Error.Validation(
        "payments.deposit.mode_invalid",
        "A deposit is held as a card imprint, in cash or by cheque.");

    public static readonly Error DepositReferenceRequired = Error.Validation(
        "payments.deposit.reference_required",
        "The authorization number of the card imprint or the cheque number is required.");

    public static readonly Error DepositAlreadySettled = Error.Conflict(
        "payments.deposit.already_settled",
        "The deposit was already released or retained.");

    public static readonly Error DepositRetainedAmountInvalid = Error.Validation(
        "payments.deposit.retained_amount_invalid",
        "The retained amount is positive and at most the amount of the deposit (release it to retain nothing).");

    public static readonly Error DepositJustificationRequired = Error.Validation(
        "payments.deposit.justification_required",
        $"A retention needs a justification of {DepositHold.JustificationMinLength} characters at least.");

    public static readonly Error DepositJustificationTooLong = Error.Validation(
        "payments.deposit.justification_too_long",
        $"A justification has {DepositHold.JustificationMaxLength} characters at most.");

    public static readonly Error DepositAttachmentTooLong = Error.Validation(
        "payments.deposit.attachment_too_long",
        $"An attachment reference has {DepositHold.AttachmentReferenceMaxLength} characters at most.");
}
