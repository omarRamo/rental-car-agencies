using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace CarRental.Web.Components.Public.Site;

/// <summary>
/// JSON-LD description of the agency for search engines (schema.org <c>AutoRental</c>, a <c>LocalBusiness</c>): name,
/// address, phone, e-mail, coordinates when known, daily opening hours, price range of the published grid and the
/// WhatsApp contact. The encoder escapes the HTML-sensitive characters, so the JSON can be inlined in a script block.
/// </summary>
public static class AgencyStructuredData
{
    private static readonly string[] Days = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Indented = false,
    };

    /// <param name="agency">The agency of the site.</param>
    /// <param name="brandName">Name shown to customers (<c>PublicSite:AgencyName</c>).</param>
    /// <param name="siteUrl">Absolute base address of the site (with a trailing slash).</param>
    /// <param name="homeUrl">Absolute address of the home page in the current culture.</param>
    /// <param name="whatsAppNumber">International number without <c>+</c>, or <see langword="null"/>.</param>
    public static string Build(PublicAgency agency, string brandName, Uri siteUrl, Uri homeUrl, string? whatsAppNumber)
    {
        ArgumentNullException.ThrowIfNull(agency);
        ArgumentNullException.ThrowIfNull(siteUrl);
        ArgumentNullException.ThrowIfNull(homeUrl);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            var info = agency.Agency;
            writer.WriteStartObject();
            writer.WriteString("@context", "https://schema.org");
            writer.WriteString("@type", "AutoRental");
            writer.WriteString("@id", new Uri(siteUrl, "#agency").AbsoluteUri);
            writer.WriteString("name", brandName);
            writer.WriteString("url", homeUrl.AbsoluteUri);
            WriteIfPresent(writer, "telephone", info.Phone);
            WriteIfPresent(writer, "email", info.Email);

            writer.WriteStartObject("address");
            writer.WriteString("@type", "PostalAddress");
            WriteIfPresent(writer, "streetAddress", info.Address);
            writer.WriteString("addressLocality", info.City);
            writer.WriteString("addressCountry", "TN");
            writer.WriteEndObject();

            if (info.Latitude is { } latitude && info.Longitude is { } longitude)
            {
                writer.WriteStartObject("geo");
                writer.WriteString("@type", "GeoCoordinates");
                writer.WriteNumber("latitude", latitude);
                writer.WriteNumber("longitude", longitude);
                writer.WriteEndObject();
            }

            if (AgencyLinks.MapUrl(info) is { } map)
            {
                writer.WriteString("hasMap", map);
            }

            writer.WriteStartArray("openingHoursSpecification");
            writer.WriteStartObject();
            writer.WriteString("@type", "OpeningHoursSpecification");
            writer.WriteStartArray("dayOfWeek");
            foreach (var day in Days)
            {
                writer.WriteStringValue(day);
            }

            writer.WriteEndArray();
            writer.WriteString("opens", PublicFormat.Time(agency.OpeningHours.OpensAt));
            writer.WriteString("closes", PublicFormat.Time(agency.OpeningHours.ClosesAt));
            writer.WriteEndObject();
            writer.WriteEndArray();

            if (agency.DailyPriceRange is { } range)
            {
                writer.WriteString(
                    "priceRange",
                    string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(range.Minimum.Amount)}-{Math.Ceiling(range.Maximum.Amount)} TND"));
            }

            writer.WriteString("currenciesAccepted", "TND");
            if (AgencyLinks.WhatsAppUrl(whatsAppNumber) is { } whatsApp)
            {
                writer.WriteStartArray("sameAs");
                writer.WriteStringValue(whatsApp);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(name, value);
        }
    }
}

/// <summary>Outbound links of the agency: map, WhatsApp.</summary>
public static class AgencyLinks
{
    /// <summary>Map of the office: by coordinates when known, else by address; <see langword="null"/> without either.</summary>
    public static string? MapUrl(Application.Referential.Contracts.AgencyInfo agency)
    {
        ArgumentNullException.ThrowIfNull(agency);
        if (agency.Latitude is { } latitude && agency.Longitude is { } longitude)
        {
            return string.Create(CultureInfo.InvariantCulture, $"https://www.google.com/maps/search/?api=1&query={latitude},{longitude}");
        }

        return string.IsNullOrWhiteSpace(agency.Address)
            ? null
            : $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString($"{agency.Address}, {agency.City}, Tunisie")}";
    }

    /// <summary>WhatsApp chat link (<c>https://wa.me/21600000000</c>), with an optional pre-filled message.</summary>
    public static string? WhatsAppUrl(string? number, string? message = null)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return null;
        }

        var link = $"https://wa.me/{number.Trim()}";
        return string.IsNullOrWhiteSpace(message) ? link : $"{link}?text={Uri.EscapeDataString(message)}";
    }
}
