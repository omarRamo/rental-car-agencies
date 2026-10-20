using System.Collections;
using System.Globalization;
using System.Resources;

namespace CarRental.Architecture.Tests;

/// <summary>
/// The .resx resources of the web project, read from the compiled assemblies: French is the neutral language (main
/// assembly), English the <c>en</c> satellite assembly. A resource found only through culture fallback does not count.
/// </summary>
internal static class ResourceCatalog
{
    private const string Extension = ".resources";

    public static readonly CultureInfo French = CultureInfo.InvariantCulture;

    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    public const string SharedResources = "CarRental.Web.Resources.SharedResources";

    /// <summary>Base names of the French resources, e.g. <c>CarRental.Web.Resources.SharedResources</c>.</summary>
    public static IReadOnlyList<string> FrenchBaseNames() => BaseNames(Assemblies.Web.GetManifestResourceNames(), Extension);

    /// <summary>Base names of the English resources (satellite assembly).</summary>
    public static IReadOnlyList<string> EnglishBaseNames() =>
        BaseNames(Assemblies.Web.GetSatelliteAssembly(English).GetManifestResourceNames(), $".{English.Name}{Extension}");

    /// <summary>The entries of one resource file in one language, without fallback to another language.</summary>
    public static IReadOnlyDictionary<string, string> Entries(string baseName, CultureInfo culture)
    {
        var manager = new ResourceManager(baseName, Assemblies.Web);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No {culture.Name} resources for {baseName}.");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => e.Value as string ?? string.Empty, StringComparer.Ordinal);
    }

    private static List<string> BaseNames(IEnumerable<string> manifestNames, string suffix) =>
        [
            .. manifestNames
                .Where(n => n.EndsWith(suffix, StringComparison.Ordinal))
                .Select(n => n[..^suffix.Length])
                .Order(StringComparer.Ordinal),
        ];
}
