using CarRental.Application.Referential;
using CarRental.Application.Referential.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Tests.Referential;

public sealed class SettingDefinitionCatalogTests
{
    [Fact]
    public void Referential_module_declares_every_well_known_setting()
    {
        using var provider = new ServiceCollection().AddReferentialModule().BuildServiceProvider();

        var catalog = provider.GetRequiredService<ISettingDefinitionCatalog>();

        catalog.All.Select(d => d.Key).ShouldBe(WellKnownSettings.All.Select(d => d.Key), ignoreOrder: true);
        catalog.Find(WellKnownSettings.VatRate.Key).ShouldBeSameAs(WellKnownSettings.VatRate);
        catalog.Find("unknown.key").ShouldBeNull();
    }

    [Fact]
    public void Modules_add_their_own_settings_ordered_by_key()
    {
        var moduleSetting = SettingDefinitions.Number("aaa.module_setting", 1m, 0m, 10m);
        using var provider = new ServiceCollection()
            .AddReferentialModule()
            .AddSettingDefinitions([moduleSetting])
            .BuildServiceProvider();

        var catalog = provider.GetRequiredService<ISettingDefinitionCatalog>();

        catalog.All[0].ShouldBeSameAs(moduleSetting);
        catalog.All.Select(d => d.Key).ShouldBe(catalog.All.Select(d => d.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Declaring_the_same_definition_twice_is_harmless()
    {
        using var provider = new ServiceCollection()
            .AddSettingDefinitions([WellKnownSettings.VatRate])
            .AddSettingDefinitions([WellKnownSettings.VatRate])
            .BuildServiceProvider();

        provider.GetRequiredService<ISettingDefinitionCatalog>().All.Count.ShouldBe(1);
    }

    [Fact]
    public void Two_different_definitions_with_one_key_are_rejected()
    {
        using var provider = new ServiceCollection()
            .AddSettingDefinitions([SettingDefinitions.Number("dup.key", 1m, 0m, 10m)])
            .AddSettingDefinitions([SettingDefinitions.Number("dup.key", 2m, 0m, 10m)])
            .BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<ISettingDefinitionCatalog>())
            .Message.ShouldContain("dup.key");
    }
}
