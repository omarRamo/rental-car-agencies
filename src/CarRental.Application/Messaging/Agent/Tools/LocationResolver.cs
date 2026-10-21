using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Messaging.Agent;

namespace CarRental.Application.Messaging.Agent.Tools;

/// <summary>
/// Turns a place said by a customer into a pick-up or return location of the agency: its code (<c>TUN</c>, <c>NBE</c>,
/// <c>AGC</c>…), its name, or a usual way to name it — the airports Tunis-Carthage (TUN), Enfidha-Hammamet (NBE),
/// Monastir (MIR) and Djerba (DJE), « l'agence » for the agency office. A place the agency does not serve, or an
/// ambiguous one, is no location: the tool asks the model to clarify, with the list of the places.
/// </summary>
internal static class LocationResolver
{
    private static readonly (string Code, string[] Words)[] Aliases =
    [
        ("TUN", ["carthage", "tunis carthage", "aeroport de tunis", "aeroport tunis", "tunis airport", "مطار تونس", "مطار قرطاج", "قرطاج"]),
        ("NBE", ["enfidha", "enfida", "hammamet airport", "aeroport d'enfidha", "مطار النفيضة", "النفيضة"]),
        ("MIR", ["monastir", "مطار المنستير", "المنستير"]),
        ("DJE", ["djerba", "jerba", "zarzis", "مطار جربة", "جربة"]),
    ];

    private static readonly string[] OfficeWords = ["agence", "bureau", "office", "agency", "centre ville", "centre-ville", "downtown", "الوكالة", "وكالة", "l'agence"];

    /// <summary>The location for <paramref name="place"/>, or <see langword="null"/> (unknown or ambiguous).</summary>
    public static LocationInfo? Resolve(string? place, IReadOnlyList<LocationInfo> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var said = AgentText.ForMatching(place);
        if (said.Length == 0 || locations.Count == 0)
        {
            return null;
        }

        var byCode = locations.FirstOrDefault(l => string.Equals(l.Code, said, StringComparison.OrdinalIgnoreCase));
        if (byCode is not null)
        {
            return byCode;
        }

        var byName = locations
            .Where(l => AgentText.ForMatching(l.Name) == said || (said.Length >= 4 && AgentText.ForMatching(l.Name).Contains(said, StringComparison.Ordinal)))
            .ToList();
        if (byName.Count == 1)
        {
            return byName[0];
        }

        foreach (var (code, words) in Aliases)
        {
            if (words.Any(w => said.Contains(AgentText.ForMatching(w), StringComparison.Ordinal)))
            {
                return locations.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (OfficeWords.Any(w => said.Contains(AgentText.ForMatching(w), StringComparison.Ordinal)))
        {
            var offices = locations.Where(l => l.Kind == LocationKindInfo.AgencyOffice).ToList();
            return offices.Count == 1 ? offices[0] : null;
        }

        var airports = locations.Where(l => l.Kind == LocationKindInfo.Airport).ToList();
        if (airports.Count == 1 && (said.Contains("aeroport", StringComparison.Ordinal) || said.Contains("airport", StringComparison.Ordinal) || said.Contains("مطار", StringComparison.Ordinal)))
        {
            return airports[0];
        }

        return null;
    }

    /// <summary>The places of the agency as the tools list them (code, name, kind).</summary>
    public static IReadOnlyList<object> Describe(IReadOnlyList<LocationInfo> locations) =>
        [.. locations.Select(l => new { code = l.Code, nom = l.Name, type = KindName(l.Kind) })];

    public static string KindName(LocationKindInfo kind) => kind switch
    {
        LocationKindInfo.AgencyOffice => "agence",
        LocationKindInfo.Airport => "aéroport",
        _ => "point de livraison",
    };
}
