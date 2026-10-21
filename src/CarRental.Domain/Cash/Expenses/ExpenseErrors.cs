using CarRental.Domain.Common;

namespace CarRental.Domain.Cash.Expenses;

/// <summary>Business errors of the expenses (codes <c>cash.expense.*</c>; texts in <c>SharedResources</c> as <c>Error.{code}</c>).</summary>
public static class ExpenseErrors
{
    public static readonly Error Forbidden = Error.Forbidden(
        "cash.expense.forbidden",
        "The current user lacks the permission to manage the expenses.");

    public static readonly Error NotFound = Error.NotFound("cash.expense.not_found", "The expense does not exist.");

    public static readonly Error AgencyNotFound = Error.NotFound("cash.expense.agency_not_found", "The agency does not exist or is inactive.");

    public static readonly Error DateInvalid = Error.Validation(
        "cash.expense.date_invalid",
        "The date of an expense is a past or present day (Tunis), from the year 2000.");

    public static readonly Error CategoryInvalid = Error.Validation("cash.expense.category_invalid", "Unknown category of expense.");

    public static readonly Error SupplierInvalid = Error.Validation(
        "cash.expense.supplier_invalid",
        $"An expense names its supplier ({Expense.SupplierMinLength} to {Expense.SupplierMaxLength} characters).");

    public static readonly Error TextTooLong = Error.Validation(
        "cash.expense.text_too_long",
        $"Too long: tax id {Expense.TaxIdMaxLength}, reference {Expense.ReferenceMaxLength}, description {Expense.DescriptionMaxLength} characters at most.");

    public static readonly Error AmountInvalid = Error.Validation(
        "cash.expense.amount_invalid",
        "Amounts are in dinars with millimes at most: the amount VAT included is positive (at most 1 000 000), the VAT zero or positive and at most that amount.");

    public static readonly Error VatRateInvalid = Error.Validation(
        "cash.expense.vat_rate_invalid",
        "The VAT rate is between 0 and 100 %.");

    public static readonly Error AmountsInconsistent = Error.Validation(
        "cash.expense.amounts_inconsistent",
        "The amount excluding VAT plus the VAT must equal the amount VAT included.");

    public static readonly Error PaymentMethodInvalid = Error.Validation("cash.expense.payment_method_invalid", "Unknown means of payment.");

    public static readonly Error ChequeNumberRequired = Error.Validation(
        "cash.expense.cheque_number_required",
        "An expense paid by cheque needs the number of the cheque.");

    public static readonly Error VehicleNotAllowed = Error.Validation(
        "cash.expense.vehicle_not_allowed",
        "Only an expense of the fleet (fuel, maintenance, repairs, insurance, road tax, leasing, other) is linked to a vehicle.");

    public static readonly Error VehicleNotFound = Error.NotFound("cash.expense.vehicle_not_found", "The vehicle is not a vehicle of the agency.");

    public static readonly Error LinkInvalid = Error.Validation(
        "cash.expense.link_invalid",
        "An expense is linked to a maintenance or a claim of its vehicle, not both.");

    public static readonly Error MaintenanceNotFound = Error.NotFound(
        "cash.expense.maintenance_not_found",
        "The maintenance is not a maintenance of the vehicle.");

    public static readonly Error ClaimNotFound = Error.NotFound("cash.expense.claim_not_found", "The claim is not a claim of the vehicle.");

    public static readonly Error CashSessionRequired = Error.Validation(
        "cash.expense.cash_session_required",
        "An expense paid in cash goes through the open cash session of the user.");

    public static readonly Error CashAmountsLocked = Error.Conflict(
        "cash.expense.cash_locked",
        "An expense paid in cash is in a cash session: its date, amounts and means of payment no longer change (delete it and record it again).");

    public static readonly Error PaymentMethodChangeNotAllowed = Error.Conflict(
        "cash.expense.payment_method_change_not_allowed",
        "An expense paid by bank cannot become an expense paid in cash (record a new one).");

    public static readonly Error Deleted = Error.Conflict("cash.expense.deleted", "The expense was deleted.");

    public static readonly Error DeletionReasonRequired = Error.Validation(
        "cash.expense.deletion_reason_required",
        $"Deleting an expense needs a reason of {Expense.ReasonMinLength} to {Expense.ReasonMaxLength} characters.");

    public static readonly Error ReceiptInvalid = Error.Validation(
        "cash.expense.receipt_invalid",
        "The receipt must be a JPEG, PNG, WebP image or a PDF of 10 MB at most.");

    public static readonly Error ReceiptNotFound = Error.NotFound("cash.expense.receipt_not_found", "The expense has no receipt.");

    public static readonly Error Stale = Error.Conflict(
        "cash.expense.stale",
        "The expense was changed by someone else in the meantime; reload it and try again.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "cash.expense.concurrent_update",
        "Another expense was recorded at the same instant; try again.");
}
