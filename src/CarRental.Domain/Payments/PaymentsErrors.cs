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

    public static readonly Error AgencyNotFound = Error.NotFound("payments.agency_not_found", "The agency does not exist or is inactive.");

    public static readonly Error BookingRequired = Error.Validation("payments.booking_required", "A booking is required.");

    /// <summary>
    /// The rental file does not exist for the operation, or belongs to another agency than the money (ADR-0067): one
    /// answer for both, which never tells that the file exists elsewhere.
    /// </summary>
    public static readonly Error BookingNotFound = Error.NotFound("payments.booking_not_found", "The rental file does not exist.");

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

    public static readonly Error RefundNotOnline = Error.Conflict(
        "payments.refund.not_online",
        "Only a captured Clic to Pay payment can be refunded through the payment gateway.");

    public static readonly Error RefundNotPending = Error.Conflict(
        "payments.refund.not_pending",
        "The online refund is no longer pending (already confirmed, failed or rejected).");

    public static readonly Error RefundNotAwaitingApproval = Error.Conflict(
        "payments.refund.not_awaiting_approval",
        "The online refund does not wait for an approval.");

    public static readonly Error RefundApprovalRequired = Error.Conflict(
        "payments.refund.approval_required",
        "This online refund must be approved by a manager before it is sent to the payment gateway.");

    public static readonly Error RefundApprovalForbidden = Error.Forbidden(
        "payments.refund.approval_forbidden",
        "Only a manager allowed to approve online refunds can approve or reject this refund.");

    public static readonly Error RefundMismatch = Error.Conflict(
        "payments.refund.mismatch",
        "The refund does not belong to this payment.");

    // Online payments (Clic to Pay)
    public static readonly Error OnlinePaymentNotFound = Error.NotFound(
        "payments.online.not_found",
        "No online payment matches this order.");

    public static readonly Error OnlineFlowInvalid = Error.Validation(
        "payments.online.flow_invalid",
        "An online payment is a pre-authorization or a one-step payment.");

    public static readonly Error OnlinePathInvalid = Error.Validation(
        "payments.online.path_invalid",
        $"The return and failure addresses are local paths of the site (starting with a single '/', {Payment.LocalPathMaxLength} characters at most).");

    public static readonly Error OnlineExpiryInvalid = Error.Validation(
        "payments.online.expiry_invalid",
        "The validity of an online payment ends after its start, within 30 days.");

    public static readonly Error OnlineDescriptionTooLong = Error.Validation(
        "payments.online.description_too_long",
        $"The description sent to the payment gateway has {Payment.GatewayDescriptionMaxLength} characters at most.");

    public static readonly Error OnlineFormUrlInvalid = Error.Validation(
        "payments.online.form_url_invalid",
        "The payment gateway returned no usable payment page address.");

    public static readonly Error OnlineGatewayOrderMismatch = Error.Conflict(
        "payments.online.gateway_order_mismatch",
        "The payment is already attached to another order of the payment gateway.");

    public static readonly Error OnlineCaptureNotApplicable = Error.Conflict(
        "payments.online.capture_not_applicable",
        "Only a pre-authorization is captured by the agency; a one-step payment is captured when the customer pays.");

    public static readonly Error OnlineNotPreAuthorized = Error.Conflict(
        "payments.online.not_pre_authorized",
        "The payment gateway has not confirmed the pre-authorization of this payment.");

    public static readonly Error OnlineCaptureExceedsAuthorization = Error.Validation(
        "payments.online.capture_exceeds_authorization",
        "The captured amount is positive and at most the pre-authorized amount.");

    public static readonly Error OnlineOperationInProgress = Error.Conflict(
        "payments.online.operation_in_progress",
        "Another operation with the payment gateway is in progress on this payment; check its status again in a moment.");

    public static readonly Error OnlineNotExpired = Error.Conflict(
        "payments.online.not_expired",
        "The online payment is still valid: it cannot be closed as expired.");

    public static readonly Error OnlineAlreadyCaptured = Error.Conflict(
        "payments.online.already_captured",
        "The payment is already captured: it can no longer be reversed, only refunded.");

    public static readonly Error OnlineCaptureUnconfirmed = Error.Conflict(
        "payments.online.capture_unconfirmed",
        "The payment gateway does not confirm the capture yet; the status is checked again automatically.");

    public static readonly Error OnlineReversalUnconfirmed = Error.Conflict(
        "payments.online.reversal_unconfirmed",
        "The payment gateway does not confirm the reversal yet; the status is checked again automatically.");

    // Payment gateway (transport and answers)
    public static readonly Error GatewayDisabled = Error.Failure(
        "payments.gateway.disabled",
        "Online payment is not enabled on this platform (ClicToPay:Enabled).");

    public static readonly Error GatewayUnavailable = Error.Failure(
        "payments.gateway.unavailable",
        "The payment gateway did not answer (network failure or time-out); the request may or may not have been processed.");

    public static readonly Error GatewayRejected = Error.Failure(
        "payments.gateway.rejected",
        "The payment gateway refused the operation (error code in the call log).");

    public static readonly Error GatewayInvalidResponse = Error.Failure(
        "payments.gateway.invalid_response",
        "The answer of the payment gateway could not be understood.");

    public static readonly Error GatewayAmountNotRepresentable = Error.Validation(
        "payments.gateway.amount_not_representable",
        "The amount cannot be expressed in the minor unit configured for the payment gateway (ClicToPay:AmountMultiplier).");

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
