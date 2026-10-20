using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Public;

/// <summary>The demo agency seen by the public site, search dates relative to today, and readers of the rendered pages.</summary>
internal static partial class PublicTestData
{
    public const string DemoAgencyName = "Agence Démo Tunis";

    /// <summary>A browser user agent (robots are not measured).</summary>
    public const string BrowserUserAgent = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Mobile Safari/537.36";

    public static async Task<(AgencyInfo Agency, IReadOnlyList<LocationInfo> Locations, IReadOnlyList<VehicleCategoryInfo> Categories)> DemoAgencyAsync(
        IntegrationHost host,
        CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope();
        var referential = scope.ServiceProvider.GetRequiredService<IReferentialQueries>();
        var agency = (await referential.GetAgenciesAsync(cancellationToken)).Single(a => a.Name == DemoAgencyName);
        var locations = await referential.GetLocationsAsync(agency.Id, cancellationToken);
        var categories = await scope.ServiceProvider.GetRequiredService<IFleetQueries>().GetCategoriesAsync(agency.Id, cancellationToken: cancellationToken);
        return (agency, locations, categories);
    }

    /// <summary>Tunis wall-clock date and time <paramref name="days"/> days from today, as written in a deep link.</summary>
    public static string LocalDate(int days, int hour = 10) =>
        TunisTime.Today(TimeProvider.System).AddDays(days).ToDateTime(new TimeOnly(hour, 0)).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The instant of a deep-link date.</summary>
    public static DateTimeOffset Instant(string localDate) =>
        TunisTime.FromLocal(DateTime.ParseExact(localDate, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));

    /// <summary>The ACRISS codes of the offers of a results page, in display order.</summary>
    public static IReadOnlyList<string> OfferCodes(string html) => [.. OfferTitle().Matches(html).Select(m => m.Groups["code"].Value)];

    /// <summary>The total TTC of each offer of a results page, decoded (<c>389,520 TND</c>).</summary>
    public static IReadOnlyList<string> OfferTotals(string html) =>
        [.. OfferAmount().Matches(html).Select(m => Plain(WebUtility.HtmlDecode(m.Groups["amount"].Value)))];

    /// <summary>Spaces of the culture (no-break, narrow no-break) as plain spaces.</summary>
    public static string Plain(string text) => text.Replace(' ', ' ').Replace(' ', ' ');

    [GeneratedRegex("<h3 id=\"offer-(?<code>[A-Z]{4})\"")]
    private static partial Regex OfferTitle();

    [GeneratedRegex("<strong class=\"offer__amount\">(?<amount>[^<]+)</strong>")]
    private static partial Regex OfferAmount();
}
