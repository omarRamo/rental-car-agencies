using System.Globalization;
using CarRental.Application.Cash.Contracts;
using CarRental.Domain.Accounting;
using CarRental.Domain.Cash.Expenses;
using CarRental.Domain.Common;

namespace CarRental.Web.Components.Admin.Accounting;

/// <summary>Display helpers of the expenses, reconciliation and accounting screens (current UI culture, Tunis time).</summary>
public static class AccountingFormat
{
    /// <summary>The usual VAT rates of Tunisia offered by the expense form (percent); any other rate can be typed.</summary>
    public static IReadOnlyList<decimal> CommonVatRates { get; } = [19m, 13m, 7m, 0m];

    public static string Month(AccountingMonth month) => month.FirstDay.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>The value of an <c>input type="month"</c>: <c>yyyy-MM</c>.</summary>
    public static string MonthValue(AccountingMonth month) => month.ToString();

    public static string Percent(decimal? rate) =>
        rate is { } value ? (value * 100m).ToString("0.##", CultureInfo.CurrentCulture) + " %" : "—";

    public static string Amount(Money amount) => amount.ToString("N", CultureInfo.CurrentCulture);

    /// <summary>A debit or credit cell: empty for zero.</summary>
    public static string Side(Money amount) => amount.IsZero ? string.Empty : amount.Amount.ToString("N3", CultureInfo.CurrentCulture);

    public static bool MayLinkVehicle(ExpenseCategoryInfo category) => ExpenseVehicleCosts.MayLinkVehicle((ExpenseCategory)(int)category);

    /// <summary>The amounts an expense form gives (as the service computes them), or null while incomplete.</summary>
    public static ExpenseAmounts? Preview(int mode, decimal? inclVat, decimal? exclVat, decimal? vat, decimal? ratePercent)
    {
        var rate = ratePercent is { } percent ? percent / 100m : (decimal?)null;
        var amounts = mode switch
        {
            1 when inclVat is { } ttc && rate is { } r => ExpenseAmounts.FromInclVat(Money.Tnd(ttc), r),
            2 when exclVat is { } ht && rate is { } r => ExpenseAmounts.FromExclVat(Money.Tnd(ht), r),
            3 when exclVat is { } ht && vat is { } tva => ExpenseAmounts.FromInvoice(Money.Tnd(ht), Money.Tnd(tva)),
            _ => null,
        };
        return amounts is { IsSuccess: true } ? amounts.Value : null;
    }
}
