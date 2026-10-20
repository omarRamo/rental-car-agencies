using System.Globalization;
using System.Reflection;
using System.Resources;
using CarRental.Application.Cash.Contracts;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;

namespace CarRental.Architecture.Tests.Cash;

/// <summary>Conventions of the Cash module (ADR-0011): every error and every label built from a value exists in French and English.</summary>
public sealed class CashConventionTests
{
    private static readonly CultureInfo[] Cultures = [CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en")];

    private static readonly ResourceManager Shared = new("CarRental.Web.Resources.SharedResources", Assemblies.Web);

    private static readonly ResourceManager Screens = new("CarRental.Web.Resources.Admin.Cash.CashResources", Assemblies.Web);

    private static List<Error> Errors() =>
        [.. typeof(CashErrors).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.GetValue(null)).OfType<Error>()];

    public static TheoryData<string> LabelKeys() =>
    [
        .. Enum.GetNames<CashSessionStatusInfo>().Select(s => $"Status.{s}"),
        .. Enum.GetNames<CashMovementKindInfo>().Select(k => $"Kind.{k}"),
        .. Enum.GetNames<CashPocketInfo>().Select(p => $"Pocket.{p}"),
        .. Enum.GetNames<CashFlowDirectionInfo>().Select(d => $"Direction.{d}"),
    ];

    [Fact]
    public void Every_error_code_of_the_cash_module_has_a_user_facing_text()
    {
        var errors = Errors();

        errors.Count.ShouldBeGreaterThan(20);
        foreach (var error in errors)
        {
            error.Code.ShouldStartWith("cash.");
            foreach (var culture in Cultures)
            {
                Shared.GetString($"Error.{error.Code}", culture).ShouldNotBeNullOrWhiteSpace($"Error.{error.Code} ({culture.Name})");
            }
        }
    }

    [Fact]
    public void Error_codes_of_the_cash_module_are_unique()
    {
        var codes = Errors().Select(e => e.Code).ToList();

        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(codes.Count);
    }

    [Theory]
    [MemberData(nameof(LabelKeys))]
    public void Every_value_shown_by_the_cash_screens_is_translated(string key)
    {
        foreach (var culture in Cultures)
        {
            Screens.GetString(key, culture).ShouldNotBeNullOrWhiteSpace($"{key} ({culture.Name})");
        }
    }
}
