using System.Globalization;
using System.Resources;
using CarRental.Application.Accounting.Journals;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Web.Resources.Admin.Accounting;

namespace CarRental.Web.Components.Admin.Accounting;

/// <summary>
/// <see cref="IJournalTexts"/> from <c>Resources/Admin/Accounting/AccountingResources.resx</c>, always in French (the neutral
/// language of the resources, the language of the books and of the chartered accountant): <c>Journal.Text.*</c>,
/// <c>Journal.Nature.*</c>, <c>Journal.Category.*</c>, <c>Journal.Method.*</c>, <c>Journal.DepositMode.*</c>.
/// </summary>
internal sealed class JournalTexts : IJournalTexts
{
    private static readonly ResourceManager Resources = new(typeof(AccountingResources).FullName!, typeof(AccountingResources).Assembly);

    /// <summary>The language of the books: the neutral resources (French).</summary>
    private static readonly CultureInfo Books = CultureInfo.InvariantCulture;

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    public string Text(JournalText text, params object?[] arguments)
    {
        var format = Get($"Journal.Text.{text}");
        return arguments.Length == 0 ? format : string.Format(French, format, arguments);
    }

    public string NatureName(InvoiceLineNatureInfo nature) => Get($"Journal.Nature.{nature}");

    public string CategoryName(ExpenseCategoryInfo category) => Get($"Journal.Category.{category}");

    public string MethodName(PaymentMethodInfo method) => Get($"Journal.Method.{method}");

    public string DepositModeName(DepositModeInfo mode) => Get($"Journal.DepositMode.{mode}");

    private static string Get(string key) => Resources.GetString(key, Books) ?? key;
}
