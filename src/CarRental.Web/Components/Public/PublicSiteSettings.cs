using CarRental.Application.Referential.Contracts;

namespace CarRental.Web.Components.Public;

/// <summary>
/// Business settings of the public site, declared to the setting catalogue by <c>AddPublicSite()</c> (seeded with the
/// other platform settings, edited from the back office settings later).
/// </summary>
public static class PublicSiteSettings
{
    /// <summary>
    /// Dinars for one euro, for the indicative euro equivalent shown next to the prices (3.40). Prices are always charged
    /// in TND; the equivalent is labelled "indicative" and rounded to the euro.
    /// </summary>
    public static readonly SettingDefinition<decimal> EurExchangeRate =
        SettingDefinitions.Number("public_site.eur_exchange_rate", 3.40m, 1m, 10m);

    public static IReadOnlyList<SettingDefinition> All { get; } = [EurExchangeRate];
}
