using System.Globalization;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging.Agent;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging.Agent.Tools;

/// <summary>
/// <c>rechercher_disponibilites</c>: the categories really available for a place and dates on the WhatsApp channel
/// (cached availability engine), priced one by one by the real pricing engine (total TTC, no extras), filtered by
/// passengers, bags, gearbox and budget — at most three options, the cheapest first, each with an id the quote link
/// tool takes. When nothing fits the filters, the closest available categories come with the criteria they miss; when
/// nothing is available, the nearest dates that are. The arguments update the structured need of the conversation.
/// </summary>
internal sealed partial class SearchAvailabilityTool(
    IAvailabilityQueries availability,
    IPricingService pricing,
    ILogger<SearchAvailabilityTool> logger) : IAgentTool
{
    public const string ToolName = "rechercher_disponibilites";

    public const int MaxOptions = 3;

    /// <summary>Same horizon as the public search: one year ahead, 90 days of rental at most.</summary>
    public const int MaxDaysAhead = 365;

    public const int MaxRentalDays = 90;

    private static readonly string[] DateFormats = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"];

    public string Name => ToolName;

    public string Description =>
        "Cherche les catégories de voitures réellement disponibles et leur prix total TTC calculé par le moteur de prix de l'agence, "
        + "pour un lieu et des dates de prise en charge et de retour (heure de Tunis), avec les filtres du client. Renvoie au plus "
        + "3 options : id (pour creer_lien_devis), catégorie, modèle « ou similaire », places, boîte, bagages, total TTC du séjour, "
        + "ce qu'il comprend et caution. N'utilise que ces chiffres.";

    public string InputSchema => """
        {"type":"object","properties":{"lieu_depart":{"type":"string","maxLength":100,"description":"Lieu de prise en charge : code (TUN, NBE, AGC…) ou nom du lieu dit par le client."},"lieu_retour":{"type":"string","maxLength":100,"description":"Lieu de retour, seulement s'il diffère du lieu de prise en charge."},"depart":{"type":"string","maxLength":25,"description":"Date et heure de prise en charge, heure de Tunis, format AAAA-MM-JJTHH:MM."},"retour":{"type":"string","maxLength":25,"description":"Date et heure de retour, heure de Tunis, format AAAA-MM-JJTHH:MM."},"passagers":{"type":"integer","minimum":1,"maximum":50,"description":"Nombre de passagers, si le client l'a dit."},"bagages":{"type":"integer","minimum":0,"maximum":50,"description":"Nombre de bagages, si le client l'a dit."},"boite":{"type":"string","enum":["automatique","manuelle"],"description":"Boîte souhaitée, si le client l'a dit."},"budget_max_tnd":{"type":"number","minimum":0,"maximum":100000,"description":"Budget maximum du séjour en TND, si le client l'a donné."}},"required":["lieu_depart","depart","retour"],"additionalProperties":false}
        """;

    public async Task<AgentToolOutcome> RunAsync(ToolArguments arguments, AgentTurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        var locations = context.Agency.Locations;
        var pickupSaid = arguments.String("lieu_depart");
        var returnSaid = arguments.String("lieu_retour");
        var pickup = LocationResolver.Resolve(pickupSaid, locations);
        var dropOff = returnSaid is null ? pickup : LocationResolver.Resolve(returnSaid, locations);
        if (pickup is null || dropOff is null)
        {
            return AgentToolOutcome.Error(
                "Lieu inconnu ou ambigu : demande au client de choisir parmi les lieux de l'agence.",
                new { lieu_non_reconnu = Truncate(pickup is null ? pickupSaid : returnSaid, 60), lieux = LocationResolver.Describe(locations) });
        }

        var pickupAt = ParseLocal(arguments.String("depart"));
        var returnAt = ParseLocal(arguments.String("retour"));
        if (pickupAt is null || returnAt is null)
        {
            return AgentToolOutcome.Error("Date ou heure illisible : format AAAA-MM-JJTHH:MM, heure de Tunis (demande l'heure au client si elle manque).");
        }

        var dateError = pickupAt <= context.Now ? "Le départ est déjà passé : demande d'autres dates au client."
            : pickupAt > context.Now.AddDays(MaxDaysAhead) ? $"Le départ est à plus de {MaxDaysAhead} jours : l'agence ne vend pas si loin."
            : returnAt <= pickupAt ? "Le retour doit être après le départ."
            : returnAt - pickupAt > TimeSpan.FromDays(MaxRentalDays) ? $"Plus de {MaxRentalDays} jours de location : propose de passer la main à un agent (longue durée)."
            : null;
        if (dateError is not null)
        {
            return AgentToolOutcome.Error(dateError, new { maintenant = AgentJson.Local(context.Now) });
        }

        var passengers = arguments.Integer("passagers");
        var luggage = arguments.Integer("bagages");
        var gearbox = arguments.String("boite") switch
        {
            "automatique" => GearboxPreferenceInfo.Automatic,
            "manuelle" => GearboxPreferenceInfo.Manual,
            _ => (GearboxPreferenceInfo?)null,
        };
        var budget = arguments.Number("budget_max_tnd") is { } maximum ? Money.Tnd(maximum) : (Money?)null;
        context.Need = context.Need with
        {
            PickupPlace = pickup.Code,
            ReturnPlace = dropOff.Id == pickup.Id ? null : dropOff.Code,
            PickupAt = pickupAt,
            ReturnAt = returnAt,
            Passengers = passengers ?? context.Need.Passengers,
            Luggage = luggage ?? context.Need.Luggage,
            Gearbox = gearbox ?? context.Need.Gearbox,
            Budget = budget ?? context.Need.Budget,
        };
        if (budget is { } given)
        {
            context.Remember(given);
        }

        var agencyId = context.Agency.Agency.Id;
        var period = Period.Create(pickupAt.Value, returnAt.Value).Value;
        var available = await availability.GetAvailableCategoriesAsync(agencyId, period, SalesChannel.WhatsApp, cancellationToken);
        var priced = new List<PricedCategory>(available.Count);
        int? minimumDays = null;
        foreach (var candidate in available)
        {
            var quote = await pricing.QuoteAsync(
                new PriceRequest(agencyId, candidate.Category.Id, pickup.Id, dropOff.Id, pickupAt.Value, returnAt.Value, SalesChannel.WhatsApp),
                cancellationToken);
            if (quote.IsFailure)
            {
                if (PricingErrorCodes.TryGetRequiredMinimumDays(quote.Error, out var required))
                {
                    minimumDays = required;
                    break;
                }

                LogNotPriced(candidate.Category.AcrissCode, quote.Error.Code);
                continue;
            }

            var misses = Misses(candidate.Category, passengers ?? context.Need.Passengers, luggage ?? context.Need.Luggage, gearbox ?? context.Need.Gearbox);
            if (budget is { } limit && quote.Value.TotalInclVat > limit)
            {
                misses.Add("budget");
            }

            priced.Add(new PricedCategory(candidate.Category, quote.Value, misses));
        }

        var criteria = new
        {
            lieu_depart = $"{pickup.Code} — {pickup.Name}",
            lieu_retour = dropOff.Id == pickup.Id ? null : $"{dropOff.Code} — {dropOff.Name}",
            depart = AgentJson.Local(pickupAt.Value),
            retour = AgentJson.Local(returnAt.Value),
            passagers = passengers ?? context.Need.Passengers,
            bagages = luggage ?? context.Need.Luggage,
            boite = (gearbox ?? context.Need.Gearbox) switch { GearboxPreferenceInfo.Automatic => "automatique", GearboxPreferenceInfo.Manual => "manuelle", _ => null },
            budget_max = budget is { } b ? AgentJson.Amount(b) : null,
        };

        var matching = priced.Where(p => p.Misses.Count == 0).OrderBy(p => p.Quote.TotalInclVat.Amount).ThenBy(p => p.Category.DisplayOrder).Take(MaxOptions).ToList();
        var shown = matching.Count > 0
            ? matching
            : [.. priced.OrderBy(p => p.Misses.Count).ThenBy(p => p.Quote.TotalInclVat.Amount).ThenBy(p => p.Category.DisplayOrder).Take(MaxOptions)];
        var prefix = context.State.NextOptionPrefix();
        var options = shown.Select((p, index) => ToOption($"{prefix}{index + 1}", agencyId, p, pickup, dropOff, pickupAt.Value, returnAt.Value)).ToList();
        context.State.RecordSearch(options, context.Now);
        context.SearchOptions = options;
        foreach (var p in shown)
        {
            context.Remember(AmountsOf(p.Quote));
        }

        if (options.Count > 0)
        {
            context.Progress = true;
            return new AgentToolOutcome(new
            {
                statut = matching.Count > 0 ? "ok" : "aucune_correspondance",
                note = matching.Count > 0
                    ? "Options disponibles, la moins chère d'abord. Présente-les dans cet ordre (1, 2, 3) ; prix garantis seulement avec creer_lien_devis."
                    : "Aucune option ne respecte tous les critères : ces catégories disponibles manquent les critères indiqués (ne_respecte_pas). Dis-le clairement au client.",
                criteres = criteria,
                options = shown.Select((p, index) => Describe(options[index], p)).ToList(),
            });
        }

        if (minimumDays is { } days)
        {
            return new AgentToolOutcome(new
            {
                statut = "duree_minimale",
                note = $"Ces dates demandent au moins {days} jours de location : propose au client d'allonger le séjour.",
                criteres = criteria,
                jours_minimum = days,
            });
        }

        if (available.Count == 0)
        {
            return new AgentToolOutcome(new
            {
                statut = "aucune_disponibilite",
                note = "Aucune voiture disponible à ces dates. N'invente aucune option ; propose les dates proches si elles existent, ou un agent.",
                criteres = criteria,
                dates_proches = await NearbyDatesAsync(agencyId, pickupAt.Value, returnAt.Value, context.Now, cancellationToken),
            });
        }

        return new AgentToolOutcome(new
        {
            statut = "prix_indisponibles",
            note = "Les prix ne peuvent pas être calculés pour l'instant : propose de passer la main à un agent.",
            criteres = criteria,
        });
    }

    private static List<string> Misses(VehicleCategoryInfo category, int? passengers, int? luggage, GearboxPreferenceInfo? gearbox)
    {
        var misses = new List<string>();
        if (passengers is { } people && category.Seats < people)
        {
            misses.Add("passagers");
        }

        if (luggage is { } bags && category.LargeBags + category.SmallBags < bags)
        {
            misses.Add("bagages");
        }

        if (gearbox is { } wished && (wished == GearboxPreferenceInfo.Automatic) != (category.Transmission == TransmissionInfo.Automatic))
        {
            misses.Add("boite");
        }

        return misses;
    }

    private static AgentOption ToOption(string id, Guid agencyId, PricedCategory priced, LocationInfo pickup, LocationInfo dropOff, DateTimeOffset pickupAt, DateTimeOffset returnAt) =>
        new(
            id,
            agencyId,
            priced.Category.Id,
            priced.Category.AcrissCode,
            priced.Category.Name,
            pickup.Id,
            pickup.Code,
            dropOff.Id,
            dropOff.Code,
            pickupAt,
            returnAt,
            priced.Quote.Days,
            priced.Quote.TotalInclVat,
            priced.Quote.Deposit);

    private static object Describe(AgentOption option, PricedCategory priced)
    {
        var category = priced.Category;
        var quote = priced.Quote;
        return new
        {
            id = option.Id,
            categorie = category.Name,
            code = category.AcrissCode,
            modele = $"{category.ExampleModel} ou similaire",
            places = category.Seats,
            portes = category.Doors,
            boite = category.Transmission == TransmissionInfo.Automatic ? "automatique" : "manuelle",
            bagages = $"{category.LargeBags} grand(s), {category.SmallBags} petit(s)",
            climatisation = category.AirConditioning,
            total_ttc = AgentJson.Amount(quote.TotalInclVat),
            jours = quote.Days,
            par_jour_ttc = AgentJson.Amount(PerDay(quote)),
            comprend = Includes(quote),
            caution = AgentJson.Amount(quote.Deposit),
            kilometrage_assurance = "Non précisés par le moteur de prix : voir infos_agence (sujet « conditions »).",
            ne_respecte_pas = priced.Misses.Count == 0 ? null : priced.Misses,
        };
    }

    /// <summary>What the total includes, line by line, as the engine priced it (the amounts are those of the quote).</summary>
    private static List<string> Includes(PriceQuote quote)
    {
        var includes = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{quote.Days} jour(s) de location") };
        foreach (var line in quote.Lines.Where(l => l.Kind != QuoteLineKind.Rental))
        {
            var label = line.Kind switch
            {
                QuoteLineKind.DurationDiscount => "remise de durée",
                QuoteLineKind.Promotion => "promotion",
                QuoteLineKind.DailyContribution => "contribution journalière de l'État",
                QuoteLineKind.Delivery => "livraison au lieu choisi",
                QuoteLineKind.OneWay => "aller simple",
                QuoteLineKind.AfterHours => "supplément hors horaires d'ouverture",
                QuoteLineKind.GuaranteedModel => "modèle garanti",
                QuoteLineKind.YoungDriver => "jeune conducteur",
                QuoteLineKind.FranchiseWaiver => "rachat de franchise",
                _ => "option",
            };
            includes.Add($"{label} : {AgentJson.Amount(line.InclVat)}");
        }

        if (quote.Lines.Select(l => l.VatRate).Where(r => r > 0m).Distinct().ToList() is [var rate])
        {
            includes.Add(string.Create(CultureInfo.InvariantCulture, $"TVA {rate * 100m:0.##} % incluse"));
        }

        return includes;
    }

    private static Money PerDay(PriceQuote quote) => Money.Tnd(quote.TotalInclVat.Amount / Math.Max(1, quote.Days));

    private static IEnumerable<decimal> AmountsOf(PriceQuote quote) =>
        new[] { quote.TotalInclVat.Amount, quote.Deposit.Amount, PerDay(quote).Amount }
            .Concat(quote.Lines.SelectMany(l => new[] { l.InclVat.Amount, l.UnitPrice.Amount }));

    /// <summary>Same stay moved one or two days earlier or later, when some category is available then (no prices: a new search prices them).</summary>
    private async Task<IReadOnlyList<object>> NearbyDatesAsync(Guid agencyId, DateTimeOffset pickupAt, DateTimeOffset returnAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var nearby = new List<object>();
        foreach (var shift in new[] { -1, 1, -2, 2 })
        {
            var start = pickupAt.AddDays(shift);
            if (start <= now || nearby.Count == 2)
            {
                continue;
            }

            var end = returnAt.AddDays(shift);
            var categories = await availability.GetAvailableCategoriesAsync(agencyId, Period.Create(start, end).Value, SalesChannel.WhatsApp, cancellationToken);
            if (categories.Count > 0)
            {
                nearby.Add(new { depart = AgentJson.Local(start), retour = AgentJson.Local(end), categories = categories.Select(c => c.Category.Name).ToList() });
            }
        }

        return nearby;
    }

    private static DateTimeOffset? ParseLocal(string? value) =>
        value is not null && DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? TunisTime.FromLocal(local)
            : null;

    private static string? Truncate(string? value, int maxLength) => value is { Length: > 0 } ? (value.Length <= maxLength ? value : value[..maxLength]) : null;

    private sealed record PricedCategory(VehicleCategoryInfo Category, PriceQuote Quote, List<string> Misses);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp agent: available category {Category} could not be priced ({ErrorCode}).")]
    private partial void LogNotPriced(string category, string errorCode);
}
