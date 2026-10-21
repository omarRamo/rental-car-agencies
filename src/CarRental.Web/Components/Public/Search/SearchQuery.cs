using System.Globalization;
using System.Text;
using CarRental.Domain.Common;
using Microsoft.Extensions.Primitives;

namespace CarRental.Web.Components.Public.Search;

/// <summary>
/// Names of the search parameters, identical in every language: deep links of the WhatsApp bot, the agents and the
/// campaigns (docs/conception/phase-2-vitrine-paiement.md §1), the home form, the filters and the "Choose" form.
/// </summary>
public static class SearchParameters
{
    public const string PickupLocation = "lieu";
    public const string ReturnLocation = "retour_lieu";
    public const string Pickup = "depart";
    public const string Return = "retour";
    public const string Category = "cat";
    public const string Gearbox = "boite";
    public const string Seats = "places";
    public const string Quote = "devis";
    public const string Source = "src";
    public const string Sort = "tri";

    /// <summary>Notice shown by the results page (<c>bientot</c>: online booking opens soon).</summary>
    public const string Info = "info";

    public const string UtmSource = "utm_source";
    public const string UtmMedium = "utm_medium";
    public const string UtmCampaign = "utm_campaign";
    public const string UtmTerm = "utm_term";
    public const string UtmContent = "utm_content";

    /// <summary>Format of the dates of a deep link: Tunis wall-clock time without offset (<c>2027-07-01T10:00</c>).</summary>
    public const string DateTimeFormat = "yyyy-MM-dd'T'HH:mm";
}

/// <summary>Gearbox filter of the results (<c>boite=auto|manuelle</c>).</summary>
public enum GearboxFilter
{
    Any,
    Automatic,
    Manual,
}

/// <summary>Order of the results (<c>tri=prix|prix_desc</c>); the requested category always comes first.</summary>
public enum ResultSort
{
    PriceAscending,
    PriceDescending,
}

/// <summary>
/// A search as received from a deep link or a form, before validation: locations and dates stay raw text (validated
/// against the agency and the clock by <see cref="SearchValidator"/>); optional parameters are parsed leniently, an
/// unknown value being ignored rather than failing the search.
/// </summary>
public sealed record SearchQuery(
    string? PickupLocation,
    string? ReturnLocation,
    string? Pickup,
    string? Return,
    AcrissCode? Category,
    GearboxFilter Gearbox,
    int? MinimumSeats,
    ResultSort Sort,
    string? QuoteCode,
    TrafficSource Traffic,
    string? Info)
{
    public const string SoonInfo = "bientot";

    /// <summary>The category chosen is no longer available (booking funnel).</summary>
    public const string UnavailableInfo = "indisponible";

    /// <summary>The selection of the booking funnel expired with its price guarantee (24 hours).</summary>
    public const string ExpiredInfo = "expire";

    /// <summary>A booking funnel link that is unknown (or malformed).</summary>
    public const string UnknownLinkInfo = "lien";

    public const int MaxSeatsFilter = 9;

    /// <summary>Notices the results page knows; any other value of <c>info</c> is ignored.</summary>
    private static readonly string[] KnownInfos = [SoonInfo, UnavailableInfo, ExpiredInfo, UnknownLinkInfo];

    private const int RawValueMaxLength = 64;

    public static SearchQuery Empty { get; } = new(null, null, null, null, null, GearboxFilter.Any, null, ResultSort.PriceAscending, null, TrafficSource.Direct, null);

    /// <summary>No location nor date: the visitor has not searched yet.</summary>
    public bool IsEmpty => PickupLocation is null && Pickup is null && Return is null;

    /// <summary>Parses query string or form values; parameter names are case-insensitive, only the first value counts.</summary>
    public static SearchQuery Parse(IEnumerable<KeyValuePair<string, StringValues>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            if (!map.ContainsKey(key) && value.Count > 0 && value[0] is { } first)
            {
                map[key] = first;
            }
        }

        string? Get(string name) => map.TryGetValue(name, out var value) ? value : null;

        return new SearchQuery(
            Raw(Get(SearchParameters.PickupLocation))?.ToUpperInvariant(),
            Raw(Get(SearchParameters.ReturnLocation))?.ToUpperInvariant(),
            Raw(Get(SearchParameters.Pickup)),
            Raw(Get(SearchParameters.Return)),
            ParseCategory(Get(SearchParameters.Category)),
            ParseGearbox(Get(SearchParameters.Gearbox)),
            ParseSeats(Get(SearchParameters.Seats)),
            ParseSort(Get(SearchParameters.Sort)),
            ParseQuoteCode(Get(SearchParameters.Quote)),
            TrafficSource.Parse(Get),
            KnownInfos.FirstOrDefault(info => string.Equals(Raw(Get(SearchParameters.Info)), info, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The parameters of this search in a stable order, to rebuild a link, the hidden fields of a form or a deep link;
    /// <paramref name="includeFilters"/> adds the gearbox, seats and sort (left out by the filter form, which has its own
    /// fields). The notice is never carried over.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string?>> ToParameters(bool includeFilters = true)
    {
        var parameters = new List<KeyValuePair<string, string?>>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parameters.Add(new KeyValuePair<string, string?>(name, value));
            }
        }

        Add(SearchParameters.PickupLocation, PickupLocation);
        Add(SearchParameters.ReturnLocation, ReturnLocation);
        Add(SearchParameters.Pickup, Pickup);
        Add(SearchParameters.Return, Return);
        Add(SearchParameters.Category, Category?.Value);
        if (includeFilters)
        {
            Add(SearchParameters.Gearbox, Gearbox switch { GearboxFilter.Automatic => "auto", GearboxFilter.Manual => "manuelle", _ => null });
            Add(SearchParameters.Seats, MinimumSeats?.ToString(CultureInfo.InvariantCulture));
            Add(SearchParameters.Sort, Sort == ResultSort.PriceDescending ? "prix_desc" : null);
        }

        Add(SearchParameters.Quote, QuoteCode);
        foreach (var (name, value) in Traffic.ToParameters())
        {
            Add(name, value);
        }

        return parameters;
    }

    /// <summary>Query string of <see cref="ToParameters"/>, with its leading <c>?</c> (empty when there is nothing).</summary>
    public string ToQueryString(bool includeFilters = true) => QueryString.Create(ToParameters(includeFilters)).ToUriComponent();

    private static string? Raw(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > RawValueMaxLength ? trimmed[..RawValueMaxLength] : trimmed;
    }

    private static AcrissCode? ParseCategory(string? value) =>
        AcrissCode.Create(value) is { IsSuccess: true } code ? code.Value : null;

    private static GearboxFilter ParseGearbox(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "auto" or "automatique" or "automatic" => GearboxFilter.Automatic,
        "manuelle" or "manual" => GearboxFilter.Manual,
        _ => GearboxFilter.Any,
    };

    private static int? ParseSeats(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seats) && seats is >= 1 and <= MaxSeatsFilter ? seats : null;

    private static ResultSort ParseSort(string? value) =>
        string.Equals(value?.Trim(), "prix_desc", StringComparison.OrdinalIgnoreCase) ? ResultSort.PriceDescending : ResultSort.PriceAscending;

    /// <summary>Code of a locked quote (letters and digits, 4 to 16): passed through, honoured by the booking funnel.</summary>
    private static string? ParseQuoteCode(string? value)
    {
        var code = value?.Trim().ToUpperInvariant();
        return code is { Length: >= 4 and <= 16 } && code.All(char.IsAsciiLetterOrDigit) ? code : null;
    }
}

/// <summary>
/// Where a visitor comes from: <c>src</c> (normalized, lower case) and the UTM tags of the link. A link sent by the
/// WhatsApp bot (<c>src=whatsapp</c>) is priced and measured on the <see cref="SalesChannel.WhatsApp"/> channel.
/// </summary>
public sealed record TrafficSource(
    string? Source,
    string? UtmSource = null,
    string? UtmMedium = null,
    string? UtmCampaign = null,
    string? UtmTerm = null,
    string? UtmContent = null)
{
    public const string WhatsAppSource = "whatsapp";
    public const int SourceMaxLength = 32;
    public const int UtmMaxLength = 100;

    public static TrafficSource Direct { get; } = new(Source: null);

    public SalesChannel Channel => Source == WhatsAppSource ? SalesChannel.WhatsApp : SalesChannel.Web;

    internal static TrafficSource Parse(Func<string, string?> get) => new(
        NormalizeSource(get(SearchParameters.Source)),
        NormalizeTag(get(SearchParameters.UtmSource)),
        NormalizeTag(get(SearchParameters.UtmMedium)),
        NormalizeTag(get(SearchParameters.UtmCampaign)),
        NormalizeTag(get(SearchParameters.UtmTerm)),
        NormalizeTag(get(SearchParameters.UtmContent)));

    public IEnumerable<KeyValuePair<string, string?>> ToParameters()
    {
        yield return new(SearchParameters.Source, Source);
        yield return new(SearchParameters.UtmSource, UtmSource);
        yield return new(SearchParameters.UtmMedium, UtmMedium);
        yield return new(SearchParameters.UtmCampaign, UtmCampaign);
        yield return new(SearchParameters.UtmTerm, UtmTerm);
        yield return new(SearchParameters.UtmContent, UtmContent);
    }

    /// <summary>Lower-case letters, digits and <c>. _ -</c> (32 at most); anything else is dropped.</summary>
    internal static string? NormalizeSource(string? value)
    {
        var source = value?.Trim().ToLowerInvariant();
        return source is { Length: > 0 and <= SourceMaxLength }
               && char.IsAsciiLetterOrDigit(source[0])
               && source.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-')
            ? source
            : null;
    }

    /// <summary>Free text without control characters, cut to 100 characters.</summary>
    internal static string? NormalizeTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, UtmMaxLength));
        foreach (var c in value.Trim().Where(c => !char.IsControl(c)).Take(UtmMaxLength))
        {
            builder.Append(c);
        }

        var tag = builder.ToString().Trim();
        return tag.Length == 0 ? null : tag;
    }
}
