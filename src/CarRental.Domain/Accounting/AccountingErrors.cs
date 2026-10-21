using CarRental.Domain.Common;

namespace CarRental.Domain.Accounting;

/// <summary>Business errors of the Accounting module (codes <c>accounting.*</c>; texts in <c>SharedResources</c> as <c>Error.{code}</c>).</summary>
public static class AccountingErrors
{
    // Cross-cutting
    public static readonly Error Forbidden = Error.Forbidden(
        "accounting.forbidden",
        "The current user lacks the permission for this accounting operation.");

    public static readonly Error AgencyNotFound = Error.NotFound("accounting.agency_not_found", "The agency does not exist or is inactive.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "accounting.concurrent_update",
        "The accounting data was changed by someone else in the meantime; reload and try again.");

    public static readonly Error LockTimeout = Error.Failure(
        "accounting.lock_timeout",
        "The accounting period is being closed or written by someone else; try again in a moment.");

    // Chart of accounts
    public static readonly Error AccountNumberInvalid = Error.Validation(
        "accounting.account.number_invalid",
        $"An account number has {LedgerAccount.NumberMinLength} to {LedgerAccount.NumberMaxLength} digits, the first one from 1 to 9.");

    public static readonly Error AccountLabelInvalid = Error.Validation(
        "accounting.account.label_invalid",
        $"An account needs a label of {LedgerAccount.LabelMaxLength} characters at most.");

    public static readonly Error AccountNumberTaken = Error.Conflict(
        "accounting.account.number_taken",
        "An account with this number already exists in the chart.");

    public static readonly Error AccountNotFound = Error.NotFound("accounting.account.not_found", "The account does not exist in the chart.");

    public static readonly Error AccountInUse = Error.Conflict(
        "accounting.account.in_use",
        "The account is used by the mapping of an operation type: map the operation to another account first.");

    // Mapping
    public static readonly Error MappingKeyUnknown = Error.Validation("accounting.mapping.key_unknown", "Unknown operation type of the mapping.");

    public static readonly Error MappingAccountInvalid = Error.Validation(
        "accounting.mapping.account_invalid",
        "An operation type is mapped to an active account of the chart.");

    public static readonly Error MappingMissing = Error.Conflict(
        "accounting.mapping.missing",
        "An operation of the period has no account in the mapping (or its account is inactive): complete the mapping.");

    // Journal entries
    public static readonly Error EntryUnbalanced = Error.Validation(
        "accounting.entry.unbalanced",
        "The debits of a journal entry must equal its credits.");

    public static readonly Error EntryEmpty = Error.Validation("accounting.entry.empty", "A journal entry needs at least one debit and one credit.");

    public static readonly Error EntryInvalid = Error.Validation(
        "accounting.entry.invalid",
        "A journal entry needs a date, a piece, a label and lines with an account and an amount in dinars.");

    // Periods and monthly closing
    public static readonly Error PeriodClosed = Error.Conflict(
        "accounting.period.closed",
        "The month is closed: nothing more can be dated in it (a manager can reopen it with a reason).");

    public static readonly Error MonthInvalid = Error.Validation("accounting.period.month_invalid", "Unknown month.");

    public static readonly Error PeriodNotEnded = Error.Conflict(
        "accounting.period.not_ended",
        "A month is closed once it is over.");

    public static readonly Error PeriodAlreadyClosed = Error.Conflict("accounting.period.already_closed", "The month is already closed.");

    public static readonly Error PeriodNotClosed = Error.Conflict("accounting.period.not_closed", "The month is not closed.");

    public static readonly Error ClosingBlocked = Error.Conflict(
        "accounting.period.closing_blocked",
        "The month cannot be closed: settle the blocking points listed first.");

    public static readonly Error ClosingNotConfirmed = Error.Conflict(
        "accounting.period.closing_not_confirmed",
        "Confirm the points listed before closing the month (they changed since they were shown).");

    public static readonly Error JournalsUnbalanced = Error.Failure(
        "accounting.period.journals_unbalanced",
        "The journals of the month do not balance: the month is not closed.");

    public static readonly Error ReopenReasonRequired = Error.Validation(
        "accounting.period.reopen_reason_required",
        $"Reopening a month needs a reason of {AccountingPeriod.ReasonMinLength} to {AccountingPeriod.ReasonMaxLength} characters.");

    public static readonly Error EntryOutsideMonth = Error.Validation(
        "accounting.period.entry_outside_month",
        "A frozen journal entry is dated in the month it belongs to.");

    // Reconciliations
    public static readonly Error JustificationReasonInvalid = Error.Validation(
        "accounting.justification.reason_invalid",
        $"A justification needs a reason of {ReconciliationJustification.ReasonMinLength} to {ReconciliationJustification.ReasonMaxLength} characters.");

    public static readonly Error DifferenceNotFound = Error.NotFound(
        "accounting.justification.difference_not_found",
        "The difference no longer exists in the reconciliation of the month: reload the page.");

    public static readonly Error StatementInvalid = Error.Validation(
        "accounting.statement.invalid",
        "The statement could not be read: check its columns (see the documented generic format) and its lines.");

    public static readonly Error StatementEmpty = Error.Validation("accounting.statement.empty", "The statement has no line.");

    public static readonly Error StatementTooLarge = Error.Validation(
        "accounting.statement.too_large",
        $"A statement has {MerchantStatement.MaxLines} lines and 2 MB at most.");

    public static readonly Error StatementLineInvalid = Error.Validation(
        "accounting.statement.line_invalid",
        "A statement line needs a date, an order reference, a kind and a positive amount in dinars (fee and settlement date optional).");

    public static readonly Error StatementNotFound = Error.NotFound("accounting.statement.not_found", "The statement does not exist.");

    // Exports
    public static readonly Error ExportFormatUnknown = Error.Validation("accounting.export.format_unknown", "Unknown export format.");
}
