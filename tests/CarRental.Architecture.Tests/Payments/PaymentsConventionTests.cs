using System.Globalization;
using System.Reflection;
using System.Resources;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;

namespace CarRental.Architecture.Tests.Payments;

/// <summary>Conventions of the Payments module (ADR-0011): every error and every label built from a value exists in French and English.</summary>
public sealed class PaymentsConventionTests
{
    private static readonly CultureInfo[] Cultures = [CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en")];

    private static readonly ResourceManager Shared = new("CarRental.Web.Resources.SharedResources", Assemblies.Web);

    private static readonly ResourceManager Screens = new("CarRental.Web.Resources.Admin.Payments.PaymentsResources", Assemblies.Web);

    private static List<Error> Errors() =>
        [.. typeof(PaymentsErrors).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.GetValue(null)).OfType<Error>()];

    public static TheoryData<string> LabelKeys() =>
    [
        .. Enum.GetNames<PaymentMethodInfo>().SelectMany(m => new[] { $"Method.{m}", $"Field.Reference.{m}" }),
        .. Enum.GetNames<PaymentStatusInfo>().Select(s => $"PaymentStatus.{s}"),
        .. Enum.GetNames<PaymentDirectionInfo>().Select(d => $"Direction.{d}"),
        .. Enum.GetNames<PaymentDirectionInfo>().SelectMany(d => Enum.GetNames<PaymentStatusInfo>().Select(s => $"Status.{d}.{s}")),
        .. Enum.GetNames<DepositModeInfo>().SelectMany(m => new[] { $"DepositMode.{m}", $"Field.DepositReference.{m}" }),
        .. Enum.GetNames<DepositStatusInfo>().Select(s => $"DepositStatus.{s}"),
    ];

    [Fact]
    public void Every_error_code_of_the_payments_module_has_a_user_facing_text()
    {
        var errors = Errors();

        errors.Count.ShouldBeGreaterThan(30);
        foreach (var error in errors)
        {
            error.Code.ShouldStartWith("payments.");
            foreach (var culture in Cultures)
            {
                Shared.GetString($"Error.{error.Code}", culture).ShouldNotBeNullOrWhiteSpace($"Error.{error.Code} ({culture.Name})");
            }
        }
    }

    [Fact]
    public void Error_codes_of_the_payments_module_are_unique()
    {
        var codes = Errors().Select(e => e.Code).ToList();

        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(codes.Count);
    }

    [Theory]
    [MemberData(nameof(LabelKeys))]
    public void Every_value_shown_by_the_payments_screens_is_translated(string key)
    {
        foreach (var culture in Cultures)
        {
            Screens.GetString(key, culture).ShouldNotBeNullOrWhiteSpace($"{key} ({culture.Name})");
        }
    }
}
