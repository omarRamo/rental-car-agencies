using System.Globalization;
using System.Reflection;
using System.Resources;
using CarRental.Application.Fleet;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using CarRental.Web.Resources.Admin.Fleet;

namespace CarRental.Architecture.Tests.Fleet;

/// <summary>Every fleet error and every value shown by the fleet screens has a French and an English text.</summary>
public sealed class FleetConventionTests
{
    private static readonly CultureInfo French = CultureInfo.InvariantCulture;
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    public static TheoryData<string> DisplayedEnumKeys()
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

        Add<VehicleStatus>("Status");
        Add<Transmission>("Transmission");
        Add<FuelType>("Fuel");
        Add<VehicleDocumentType>("DocumentType");
        Add<MaintenanceType>("MaintenanceType");
        Add<MaintenanceStatus>("MaintenanceStatus");
        Add<FinancingType>("Financing");
        Add<DeadlineState>("Deadline");
        Add<MaintenanceDueState>("Due");
        Add<VehicleHistoryKind>("History");
        foreach (var status in new[] { VehicleStatus.Active, VehicleStatus.Maintenance, VehicleStatus.OutOfService })
        {
            keys.Add($"Status.Hint.{status}");
        }

        return keys;
    }

    [Fact]
    public void Every_error_code_of_the_fleet_module_has_a_user_facing_text()
    {
        var manager = new ResourceManager("CarRental.Web.Resources.SharedResources", Assemblies.Web);
        var errors = typeof(FleetErrors).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.GetValue(null)).OfType<Error>().ToList();

        errors.Count.ShouldBeGreaterThan(50);
        foreach (var error in errors)
        {
            error.Code.ShouldStartWith("fleet.");
            manager.GetString($"Error.{error.Code}", French).ShouldNotBeNullOrWhiteSpace(error.Code);
            manager.GetString($"Error.{error.Code}", English).ShouldNotBeNullOrWhiteSpace(error.Code);
        }
    }

    [Theory]
    [MemberData(nameof(DisplayedEnumKeys))]
    public void Every_value_shown_by_the_fleet_screens_is_translated(string key)
    {
        var manager = new ResourceManager(typeof(FleetResources).FullName!, Assemblies.Web);

        manager.GetString(key, French).ShouldNotBeNullOrWhiteSpace(key);
        manager.GetString(key, English).ShouldNotBeNullOrWhiteSpace(key);
    }
}
