using CarRental.Application.Billing.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Paging;
using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Cash.Expenses;

/// <summary>How the amounts of an expense are entered.</summary>
public enum ExpenseAmountMode
{
    /// <summary>The amount VAT included and the rate: HT = round(TTC ÷ (1 + rate)), VAT = TTC − HT.</summary>
    InclVatAndRate = 1,

    /// <summary>The amount excluding VAT and the rate: VAT = round(HT × rate), TTC = HT + VAT.</summary>
    ExclVatAndRate = 2,

    /// <summary>The amount excluding VAT and the VAT as printed on the supplier's invoice.</summary>
    AsInvoiced = 3,
}

/// <summary>What the expense screen sends (amounts typed in dinars, three decimals at most; the rate as a fraction, 0.19).</summary>
public sealed record ExpenseForm(
    Guid AgencyId,
    DateOnly Date,
    ExpenseCategoryInfo Category,
    string? Supplier,
    ExpenseAmountMode AmountMode,
    decimal? InclVat,
    decimal? ExclVat,
    decimal? Vat,
    decimal? VatRate,
    ExpensePaymentMethodInfo PaymentMethod,
    string? PaymentReference = null,
    string? SupplierTaxId = null,
    string? Reference = null,
    string? Description = null,
    Guid? VehicleId = null,
    Guid? MaintenanceEventId = null,
    Guid? ClaimId = null);

/// <summary>Filter of the list of the expenses of an agency: dates (included), category, means, vehicle, text, deleted ones.</summary>
public sealed record ExpenseFilter(
    Guid AgencyId,
    DateOnly? From = null,
    DateOnly? To = null,
    ExpenseCategoryInfo? Category = null,
    ExpensePaymentMethodInfo? PaymentMethod = null,
    Guid? VehicleId = null,
    string? Search = null,
    bool IncludeDeleted = false);

/// <summary>An expense as the back office shows it, with whether its month is closed and how it counts in the reports.</summary>
public sealed record ExpenseView(
    Guid Id,
    Guid AgencyId,
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
    ExpenseVehicleCostRuleInfo VehicleCostRule,
    bool HasReceipt,
    string? ReceiptFileName,
    string? ReceiptContentType,
    DateTimeOffset? ReceiptUploadedAtUtc,
    DateTimeOffset RecordedAtUtc,
    string? RecordedByName,
    DateTimeOffset ChangedAtUtc,
    string? ChangedByName,
    int Revision,
    bool IsDeleted,
    DateTimeOffset? DeletedAtUtc,
    string? DeletedByName,
    string? DeletionReason,
    bool IsInClosedMonth)
{
    public bool IsPaidInCash => PaymentMethod == ExpensePaymentMethodInfo.Cash;

    /// <summary>Whether the screen offers to change or delete it.</summary>
    public bool IsEditable => !IsDeleted && !IsInClosedMonth;
}

/// <summary>A page of expenses and the totals of the whole filtered list (deleted ones never count).</summary>
public sealed record ExpenseList(PagedResult<ExpenseView> Page, int Count, Money TotalExclVat, Money TotalVat, Money TotalInclVat);

/// <summary>A vehicle of the agency an expense may name.</summary>
public sealed record ExpenseVehicleOption(Guid Id, string PlateNumber, string Label);

/// <summary>The maintenances and claims of a vehicle, which an expense of it may pay (its cost then counts once, through them).</summary>
public sealed record ExpenseLinkOptions(IReadOnlyList<MaintenanceRecordInfo> Maintenances, IReadOnlyList<ClaimSummaryInfo> Claims);

/// <summary>A stored receipt to stream to the back office.</summary>
public sealed record ExpenseReceiptFile(string Container, string Path, string ContentType, string ETag, string FileName);
