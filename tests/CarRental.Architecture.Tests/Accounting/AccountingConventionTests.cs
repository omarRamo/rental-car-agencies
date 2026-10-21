using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using CarRental.Application.Accounting.Journals;
using CarRental.Application.Accounting.Reconciliation;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Cash.Expenses;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Accounting;
using CarRental.Domain.Cash.Expenses;
using CarRental.Domain.Common;

namespace CarRental.Architecture.Tests.Accounting;

/// <summary>
/// Conventions of the pre-accounting and of the expenses (ADR-0011, ADR-0063 to ADR-0065): every error and every label
/// built from a value exists in French and English; the texts of the journals keep the same placeholders in both.
/// </summary>
public sealed partial class AccountingConventionTests
{
    private static readonly CultureInfo[] Cultures = [CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en")];

    private static readonly ResourceManager Shared = new("CarRental.Web.Resources.SharedResources", Assemblies.Web);

    private static readonly ResourceManager Accounting = new("CarRental.Web.Resources.Admin.Accounting.AccountingResources", Assemblies.Web);

    private static readonly ResourceManager Expenses = new("CarRental.Web.Resources.Admin.Expenses.ExpensesResources", Assemblies.Web);

    public static TheoryData<string, string> Errors() =>
    [
        .. ErrorsOf(typeof(AccountingErrors)).Select(e => ("accounting.", e.Code)),
        .. ErrorsOf(typeof(ExpenseErrors)).Select(e => ("cash.expense.", e.Code)),
    ];

    public static TheoryData<string> AccountingLabelKeys() =>
    [
        .. Enum.GetNames<Journal>().Select(j => $"Journal.{j}"),
        .. Enum.GetNames<JournalText>().Select(t => $"Journal.Text.{t}"),
        .. Enum.GetNames<InvoiceLineNatureInfo>().Select(n => $"Journal.Nature.{n}"),
        .. Enum.GetNames<ExpenseCategoryInfo>().Select(c => $"Journal.Category.{c}"),
        .. Enum.GetNames<PaymentMethodInfo>().Select(m => $"Journal.Method.{m}"),
        .. Enum.GetNames<DepositModeInfo>().Select(m => $"Journal.DepositMode.{m}"),
        .. AccountingMappingCatalog.All.Select(d => $"Mapping.{d.Key}"),
        .. Enum.GetNames<MappingGroup>().Select(g => $"MappingGroup.{g}"),
        .. Enum.GetNames<ClosingCheckSeverity>().Select(s => $"Severity.{s}"),
        .. Enum.GetNames<ClosingCheckKind>().Select(k => $"Check.{k}"),
        .. Enum.GetNames<AccountingPeriodStatus>().Select(s => $"PeriodStatus.{s}"),
        .. Enum.GetNames<DifferenceKind>().Select(k => $"DifferenceKind.{k}"),
        .. Enum.GetNames<StatementMatch>().Select(m => $"Match.{m}"),
        .. Enum.GetNames<StatementLineKind>().Select(k => $"LineKind.{k}"),
        .. Enum.GetNames<StatementOrigin>().Select(o => $"Origin.{o}"),
        .. Enum.GetNames<CashSessionStatusInfo>().Select(s => $"SessionStatus.{s}"),
        .. Enum.GetNames<PaymentDirectionInfo>().Select(d => $"Direction.{d}"),
        .. Enum.GetNames<GatewayAttentionInfo>().Select(a => $"Attention.{a}"),
    ];

    public static TheoryData<string> ExpenseLabelKeys() =>
    [
        .. Enum.GetNames<ExpenseCategoryInfo>().Select(c => $"Category.{c}"),
        .. Enum.GetNames<ExpensePaymentMethodInfo>().Select(m => $"Method.{m}"),
        .. Enum.GetNames<ExpenseAmountMode>().Select(m => $"AmountMode.{m}"),
        .. Enum.GetNames<ExpenseVehicleCostRuleInfo>().Select(r => $"CostRule.{r}.Help"),
        .. Enum.GetNames<MaintenanceTypeInfo>().Select(t => $"MaintenanceType.{t}"),
    ];

    public static TheoryData<string> JournalTextKeys() => [.. Enum.GetNames<JournalText>().Select(t => $"Journal.Text.{t}")];

    [Theory]
    [MemberData(nameof(Errors))]
    public void Every_error_of_the_accounting_and_of_the_expenses_has_a_user_facing_text(string prefix, string code)
    {
        code.ShouldStartWith(prefix);
        foreach (var culture in Cultures)
        {
            Shared.GetString($"Error.{code}", culture).ShouldNotBeNullOrWhiteSpace($"Error.{code} ({culture.Name})");
        }
    }

    [Fact]
    public void Error_codes_of_the_accounting_and_of_the_expenses_are_unique()
    {
        var codes = ErrorsOf(typeof(AccountingErrors)).Concat(ErrorsOf(typeof(ExpenseErrors))).Select(e => e.Code).ToList();

        codes.Count.ShouldBeGreaterThan(50);
        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(codes.Count);
    }

    [Theory]
    [MemberData(nameof(AccountingLabelKeys))]
    public void Every_value_shown_by_the_accounting_screens_is_translated(string key)
    {
        foreach (var culture in Cultures)
        {
            Accounting.GetString(key, culture).ShouldNotBeNullOrWhiteSpace($"{key} ({culture.Name})");
        }
    }

    [Theory]
    [MemberData(nameof(ExpenseLabelKeys))]
    public void Every_value_shown_by_the_expense_screens_is_translated(string key)
    {
        foreach (var culture in Cultures)
        {
            Expenses.GetString(key, culture).ShouldNotBeNullOrWhiteSpace($"{key} ({culture.Name})");
        }
    }

    [Theory]
    [MemberData(nameof(JournalTextKeys))]
    public void The_texts_of_the_journals_keep_the_same_placeholders_in_both_languages(string key)
    {
        var french = Placeholders(Accounting.GetString(key, CultureInfo.InvariantCulture)!);
        var english = Placeholders(Accounting.GetString(key, CultureInfo.GetCultureInfo("en"))!);

        english.ShouldBe(french, key);
        if (french.Count > 0)
        {
            french.ShouldBe(Enumerable.Range(0, french.Max() + 1).ToList(), $"{key}: placeholders {{0}} to {{n}}, none missing");
        }
    }

    private static List<Error> ErrorsOf(Type errors) =>
        [.. errors.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.GetValue(null)).OfType<Error>()];

    private static List<int> Placeholders(string format) =>
        [.. PlaceholderPattern().Matches(format).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order()];

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
