using System.Globalization;
using System.Reflection;
using System.Resources;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using CarRental.Web.Components.Admin.Reports;
using CarRental.Web.Resources.Admin.Reports;

namespace CarRental.Architecture.Tests.Reporting;

/// <summary>
/// Every reporting error and every value shown by the reports screen or written to its Excel exports (channels, means of
/// payment, deposit modes, sheets and file names of each export, cost sources) has a French and an English text.
/// </summary>
public sealed class ReportingConventionTests
{
    private static readonly CultureInfo French = CultureInfo.InvariantCulture;
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    private static readonly ResourceManager Screens = new(typeof(ReportsResources).FullName!, Assemblies.Web);

    private static List<Error> Errors() =>
        [.. typeof(ReportingErrors).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.GetValue(null)).OfType<Error>()];

    public static TheoryData<string> DisplayedKeys()
    {
        var keys = new TheoryData<string>();
        void Add<TEnum>(string prefix)
            where TEnum : struct, Enum
        {
            foreach (var name in Enum.GetNames<TEnum>())
            {
                keys.Add($"{prefix}.{name}");
            }
        }

        Add<SalesChannel>("Channel");
        Add<PaymentMethodInfo>("Method");
        Add<DepositModeInfo>("DepositMode");
        Add<ReportTable>("Sheet");
        Add<ReportTable>("Export.File");
        foreach (var source in CostSourceNames())
        {
            keys.Add($"Source.Cost.{source}");
        }

        return keys;
    }

    /// <summary>The <c>SourceName</c> constant of every direct cost source of the application.</summary>
    private static List<string> CostSourceNames() =>
    [
        .. Assemblies.Application.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IVehicleCostsSource).IsAssignableFrom(t))
            .Select(t => t.GetField("SourceName", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() as string)
            .OfType<string>(),
    ];

    [Fact]
    public void The_cost_sources_are_found_with_their_name()
    {
        // Positive control of the discovery used by the translation theory: the fleet costs come with Reporting.
        CostSourceNames().ShouldContain("fleet");
    }

    [Fact]
    public void Every_error_code_of_the_reporting_module_has_a_user_facing_text()
    {
        var shared = new ResourceManager("CarRental.Web.Resources.SharedResources", Assemblies.Web);
        var errors = Errors();

        errors.Count.ShouldBeGreaterThanOrEqualTo(12);
        foreach (var error in errors)
        {
            error.Code.ShouldStartWith("reporting.");
            shared.GetString($"Error.{error.Code}", French).ShouldNotBeNullOrWhiteSpace(error.Code);
            shared.GetString($"Error.{error.Code}", English).ShouldNotBeNullOrWhiteSpace(error.Code);
        }

        errors.Select(e => e.Code).Distinct(StringComparer.Ordinal).Count().ShouldBe(errors.Count);
    }

    [Theory]
    [MemberData(nameof(DisplayedKeys))]
    public void Every_value_shown_by_the_reports_is_translated(string key)
    {
        Screens.GetString(key, French).ShouldNotBeNullOrWhiteSpace(key);
        Screens.GetString(key, English).ShouldNotBeNullOrWhiteSpace(key);
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void Every_sheet_name_is_a_valid_excel_sheet_name(ReportTable table)
    {
        foreach (var culture in new[] { French, English })
        {
            var name = Screens.GetString($"Sheet.{table}", culture)!;

            name.Length.ShouldBeInRange(1, 31, $"{table} ({culture.Name})");
            name.IndexOfAny([':', '\\', '/', '?', '*', '[', ']']).ShouldBe(-1, $"{table} ({culture.Name})");
        }
    }

    public static TheoryData<ReportTable> Tables() => [.. Enum.GetValues<ReportTable>()];
}
