using CarRental.Application.Billing.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Payments.Contracts;

namespace CarRental.Application.Accounting.Journals;

/// <summary>
/// The labels of the journal entries, in the language of the books (French, the language of the chartered accountant):
/// provided by the web host from its resources, like the texts of the invoices.
/// </summary>
public interface IJournalTexts
{
    /// <summary>A label of an entry or a line, its placeholders filled with <paramref name="arguments"/>.</summary>
    string Text(JournalText text, params object?[] arguments);

    string NatureName(InvoiceLineNatureInfo nature);

    string CategoryName(ExpenseCategoryInfo category);

    string MethodName(PaymentMethodInfo method);

    string DepositModeName(DepositModeInfo mode);
}

/// <summary>The labels of the journal entries (resources <c>Journal.Text.{name}</c>).</summary>
public enum JournalText
{
    /// <summary>{0} number, {1} buyer.</summary>
    Invoice = 1,

    /// <summary>{0} number, {1} buyer.</summary>
    CreditNote = 2,

    /// <summary>{0} rate in percent, {1} piece.</summary>
    SalesVat = 3,

    /// <summary>{0} piece.</summary>
    SalesStampDuty = 4,

    /// <summary>{0} nature, {1} piece.</summary>
    SalesRevenue = 5,

    /// <summary>{0} number, {1} supplier.</summary>
    Expense = 6,

    /// <summary>{0} category, {1} supplier.</summary>
    ExpenseCharge = 7,

    /// <summary>{0} supplier.</summary>
    ExpenseVat = 8,

    /// <summary>{0} number, {1} supplier, {2} means.</summary>
    ExpensePayment = 9,

    /// <summary>{0} means, {1} file.</summary>
    Payment = 10,

    /// <summary>{0} means, {1} file.</summary>
    Refund = 11,

    /// <summary>{0} file.</summary>
    ChannelPrepaid = 12,

    /// <summary>{0} mode, {1} file.</summary>
    DepositTaken = 13,

    /// <summary>{0} mode, {1} file, {2} retained, {3} returned.</summary>
    DepositSettled = 14,

    /// <summary>{0} mode, {1} file.</summary>
    DepositCollected = 15,

    /// <summary>{0} file.</summary>
    CashPayment = 16,

    /// <summary>{0} file.</summary>
    CashRefund = 17,

    /// <summary>{0} file.</summary>
    CashDepositReceived = 18,

    /// <summary>{0} file.</summary>
    CashDepositReturned = 19,

    /// <summary>{0} file.</summary>
    CashDepositRetained = 20,

    /// <summary>{0} reason.</summary>
    CashManualIn = 21,

    /// <summary>{0} reason.</summary>
    CashManualOut = 22,

    /// <summary>{0} reason.</summary>
    CashAdjustment = 23,

    /// <summary>{0} expense number.</summary>
    CashExpensePaid = 24,

    /// <summary>{0} expense number.</summary>
    CashExpenseReversed = 25,

    /// <summary>{0} business day, {1} cashier.</summary>
    CashVariance = 26,

    /// <summary>{0} channel, {1} month (yyyy-MM).</summary>
    Payout = 27,

    /// <summary>{0} channel, {1} month.</summary>
    PayoutCommission = 28,

    /// <summary>{0} channel, {1} month.</summary>
    PayoutDifference = 29,

    /// <summary>{0} settlement day.</summary>
    ClicToPaySettlement = 30,

    /// <summary>{0} settlement day.</summary>
    ClicToPayFee = 31,

    /// <summary>{0} label of the corrected entry, {1} month it was frozen in.</summary>
    Correction = 32,
}
