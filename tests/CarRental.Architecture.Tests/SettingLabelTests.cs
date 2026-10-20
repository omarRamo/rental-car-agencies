using CarRental.Application;
using CarRental.Application.Referential.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Architecture.Tests;

/// <summary>
/// The settings screen shows every setting declared by the modules with a French and English label and a group title
/// (ADR-0029). The catalogue is read from the application registration at test time, so a setting added by any module is
/// checked as soon as it is declared.
/// </summary>
public sealed class SettingLabelTests
{
    private const string SettingLabels = "CarRental.Web.Resources.Admin.Settings.SettingLabels";

    [Fact]
    public void Every_declared_setting_has_a_label_and_a_group_title_in_french_and_english()
    {
        using var provider = new ServiceCollection().AddApplication().BuildServiceProvider();
        var definitions = provider.GetRequiredService<ISettingDefinitionCatalog>().All;
        var french = ResourceCatalog.Entries(SettingLabels, ResourceCatalog.French);
        var english = ResourceCatalog.Entries(SettingLabels, ResourceCatalog.English);

        definitions.ShouldNotBeEmpty();
        var missing = definitions
            .SelectMany(d => new[] { $"Setting.{d.Key}.Label", $"Group.{d.Group}" })
            .Distinct(StringComparer.Ordinal)
            .Where(key => !french.ContainsKey(key) || !english.ContainsKey(key))
            .ToList();

        missing.ShouldBeEmpty(
            "Declared settings need these keys in Resources/Admin/Settings/SettingLabels.resx (French) and SettingLabels.en.resx (English), "
            + "with Setting.<key>.Help for the help text");
    }
}
