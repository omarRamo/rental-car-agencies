using System.Globalization;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Resources.Public;
using Microsoft.Extensions.Localization;

namespace CarRental.Web.Components.Public.Shared;

/// <summary>A question of the FAQ and its answer, already localized.</summary>
public sealed record FaqItem(string Id, string Question, string Answer);

/// <summary>
/// The FAQ of the public site (texts in the public resources), with the figures the agency actually applies: age and
/// licence seniority, young driver threshold and deposits come from the business settings and the fleet, the pick-up
/// locations from the referential. The home page shows the first <see cref="HomeCount"/> questions.
/// </summary>
public static class FaqContent
{
    public const int HomeCount = 4;

    public static IReadOnlyList<FaqItem> Build(IStringLocalizer<PublicResources> localizer, PublicAgency? agency, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        culture ??= CultureInfo.CurrentCulture;
        var items = new List<FaqItem>
        {
            Item(localizer, "documents"),
            Item(localizer, "price"),
            Item(localizer, "payment"),
            Item(localizer, "confirmation"),
        };

        if (agency is not null)
        {
            var policies = agency.Policies;
            items.Add(Item(localizer, "age", policies.DriverMinimumAge, policies.DriverMinimumLicenseYears, policies.YoungDriverAgeBelow));
            if (agency.DepositRange is { } deposits)
            {
                items.Add(Item(localizer, "deposit", PublicFormat.Price(deposits.Minimum, culture), PublicFormat.Price(deposits.Maximum, culture)));
            }

            if (agency.Locations.Count > 0)
            {
                items.Add(Item(localizer, "locations", string.Join(", ", agency.Locations.Select(l => l.Name))));
            }
        }

        items.Add(Item(localizer, "one_way"));
        items.Add(Item(localizer, "cancellation"));
        items.Add(Item(localizer, "territory"));
        return items;
    }

    private static FaqItem Item(IStringLocalizer<PublicResources> localizer, string id, params object[] arguments) =>
        new($"faq-{id.Replace('_', '-')}", localizer[$"Faq.{id}.Question"], localizer[$"Faq.{id}.Answer", arguments]);
}
