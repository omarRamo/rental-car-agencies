using CarRental.Application.Cash.Contracts;
using CarRental.Domain.Cash.Expenses;
using CarRental.Domain.Common;

namespace CarRental.Application.Cash.Expenses;

/// <summary>Conversions between the expense of the domain and the contract types (same enum values).</summary>
internal static class ExpenseMapping
{
    public static ExpenseCategory ToDomain(this ExpenseCategoryInfo category) => (ExpenseCategory)(int)category;

    public static ExpenseCategoryInfo ToInfo(this ExpenseCategory category) => (ExpenseCategoryInfo)(int)category;

    public static ExpensePaymentMethod ToDomain(this ExpensePaymentMethodInfo method) => (ExpensePaymentMethod)(int)method;

    public static ExpensePaymentMethodInfo ToInfo(this ExpensePaymentMethod method) => (ExpensePaymentMethodInfo)(int)method;

    public static ExpenseVehicleCostRuleInfo ToInfo(this ExpenseVehicleCostRule rule) => (ExpenseVehicleCostRuleInfo)(int)rule;

    public static ExpenseEntryInfo ToEntry(this Expense expense)
    {
        ArgumentNullException.ThrowIfNull(expense);
        return new ExpenseEntryInfo(
            expense.Id,
            expense.Number,
            expense.Date,
            expense.Category.ToInfo(),
            expense.Supplier,
            expense.SupplierTaxId,
            expense.Reference,
            expense.Description,
            expense.ExclVat,
            expense.Vat,
            expense.InclVat,
            expense.VatRate,
            expense.PaymentMethod.ToInfo(),
            expense.PaymentReference,
            expense.CashSessionId,
            expense.VehicleId,
            expense.MaintenanceEventId,
            expense.ClaimId,
            expense.HasReceipt);
    }

    public static ExpenseView ToView(this Expense expense, bool isInClosedMonth)
    {
        ArgumentNullException.ThrowIfNull(expense);
        return new ExpenseView(
            expense.Id,
            expense.AgencyId,
            expense.Number,
            expense.Date,
            expense.Category.ToInfo(),
            expense.Supplier,
            expense.SupplierTaxId,
            expense.Reference,
            expense.Description,
            expense.ExclVat,
            expense.Vat,
            expense.InclVat,
            expense.VatRate,
            expense.PaymentMethod.ToInfo(),
            expense.PaymentReference,
            expense.CashSessionId,
            expense.VehicleId,
            expense.MaintenanceEventId,
            expense.ClaimId,
            expense.VehicleCostRule.ToInfo(),
            expense.HasReceipt,
            expense.ReceiptFileName,
            expense.ReceiptContentType,
            expense.ReceiptUploadedAtUtc,
            expense.RecordedAtUtc,
            expense.RecordedByName,
            expense.ChangedAtUtc,
            expense.ChangedByName,
            expense.Revision,
            expense.IsDeleted,
            expense.DeletedAtUtc,
            expense.DeletedByName,
            expense.DeletionReason,
            isInClosedMonth);
    }

    /// <summary>The amounts typed on the form, checked to the millime and computed as the mode says.</summary>
    public static Result<ExpenseAmounts> AmountsOf(ExpenseForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return form.AmountMode switch
        {
            ExpenseAmountMode.InclVatAndRate => Exact(form.InclVat).Bind(inclVat => Rate(form.VatRate).Bind(rate => ExpenseAmounts.FromInclVat(inclVat, rate))),
            ExpenseAmountMode.ExclVatAndRate => Exact(form.ExclVat).Bind(exclVat => Rate(form.VatRate).Bind(rate => ExpenseAmounts.FromExclVat(exclVat, rate))),
            ExpenseAmountMode.AsInvoiced => Exact(form.ExclVat).Bind(exclVat => Exact(form.Vat).Bind(vat => ExpenseAmounts.FromInvoice(exclVat, vat))),
            _ => ExpenseErrors.AmountInvalid,
        };
    }

    private static Result<Money> Exact(decimal? amount)
    {
        if (amount is not { } value)
        {
            return ExpenseErrors.AmountInvalid;
        }

        var money = Money.FromExact(value, Currency.Tnd);
        return money.IsSuccess ? money.Value : ExpenseErrors.AmountInvalid;
    }

    private static Result<decimal> Rate(decimal? rate) =>
        rate is { } value && value is >= 0m and <= 1m && decimal.Round(value, 4) == value ? value : ExpenseErrors.VatRateInvalid;
}

/// <summary>
/// Checks of the receipts of the expenses, the same as those of the files of the claims (ADR-0059): the real type read
/// from the first bytes (JPEG, PNG, WebP, PDF), 10 MB at most, the name sent by the device kept for display only.
/// </summary>
internal static class ExpenseReceiptFiles
{
    public const long MaxBytes = 10 * 1024 * 1024;

    public sealed record FileType(string ContentType, string Extension);

    public static string? NameOf(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? null : fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..].Trim();

    /// <summary>The whole content when it has at most <paramref name="maxBytes"/> bytes, else <see langword="null"/>.</summary>
    public static async Task<byte[]?> ReadBoundedAsync(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>JPEG, PNG, WebP or PDF from the first bytes; <see langword="null"/> otherwise.</summary>
    public static FileType? TypeOf(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0xFF, 0xD8, 0xFF, ..] => new FileType("image/jpeg", "jpg"),
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => new FileType("image/png", "png"),
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => new FileType("image/webp", "webp"),
        [0x25, 0x50, 0x44, 0x46, 0x2D, ..] => new FileType("application/pdf", "pdf"),
        _ => null,
    };
}
