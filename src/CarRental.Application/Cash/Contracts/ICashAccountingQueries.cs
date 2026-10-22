using CarRental.Domain.Common;

namespace CarRental.Application.Cash.Contracts;

/// <summary>Category of an expense (same values as the domain enum).</summary>
public enum ExpenseCategoryInfo
{
    Fuel = 1,
    Maintenance = 2,
    Repairs = 3,
    Insurance = 4,
    RoadTax = 5,
    Leasing = 6,
    CommissionBookingCom = 7,
    CommissionClicToPay = 8,
    PremisesRent = 9,
    Utilities = 10,
    Other = 11,
}

/// <summary>How an expense was paid (same values as the domain enum).</summary>
public enum ExpensePaymentMethodInfo
{
    Cash = 1,
    Card = 2,
    BankTransfer = 3,
    Cheque = 4,
}

/// <summary>Whether, and why not, an expense counts as a direct cost of its vehicle (same values as the domain enum).</summary>
public enum ExpenseVehicleCostRuleInfo
{
    Counted = 1,
    NoVehicle = 2,
    CountedByMaintenance = 3,
    CountedByClaim = 4,
    CountedByVehicleDocument = 5,
    CountedByDepreciation = 6,
}

/// <summary>
/// Read API of the Cash module for the pre-accounting and the reconciliations (package 3B): the expenses of a period and
/// the movements of the cash sessions of a period. No permission check (the caller checks its own).
/// </summary>
/// <remarks>A separate interface from <see cref="ICashQueries"/>, so that its implementers are not affected.</remarks>
public interface ICashAccountingQueries
{
    /// <summary>The expenses of an agency dated between the Tunis days <paramref name="from"/> and <paramref name="to"/> (included), deleted ones excepted, by date then number.</summary>
    Task<IReadOnlyList<ExpenseEntryInfo>> GetExpensesAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// The movements of the cash sessions of an agency whose business day lies between <paramref name="from"/> and
    /// <paramref name="to"/> (included), by day, session and sequence; a movement of an expense carries its number.
    /// </summary>
    Task<IReadOnlyList<CashMovementEntryInfo>> GetMovementsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>An expense for the purchases journal: number, date, category, supplier, amounts, means of payment and links.</summary>
public sealed record ExpenseEntryInfo(
    Guid Id,
    string Number,
    DateOnly Date,
    ExpenseCategoryInfo Category,
    string Supplier,
    string? SupplierTaxId,
    string? Reference,
    string? Description,
    Money ExclVat,
    Money Vat,
    Money InclVat,
    decimal? VatRate,
    ExpensePaymentMethodInfo PaymentMethod,
    string? PaymentReference,
    Guid? CashSessionId,
    Guid? VehicleId,
    Guid? MaintenanceEventId,
    Guid? ClaimId,
    bool HasReceipt);

/// <summary>A movement of a cash session for the cash journal, with the business day of its session.</summary>
public sealed record CashMovementEntryInfo(
    Guid MovementId,
    Guid SessionId,
    DateOnly BusinessDate,
    string CashierName,
    int Sequence,
    CashMovementKindInfo Kind,
    Money Amount,
    Money RevenueEffect,
    Money DepositEffect,
    string? Reason,
    Guid? BookingId,
    Guid? PaymentId,
    Guid? DepositId,
    Guid? ExpenseId,
    string? ExpenseNumber,
    Guid? RelatedSessionId,
    DateTimeOffset RecordedAtUtc);
