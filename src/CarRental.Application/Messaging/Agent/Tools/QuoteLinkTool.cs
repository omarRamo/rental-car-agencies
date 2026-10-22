using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging.Agent;
using Microsoft.Extensions.Options;

namespace CarRental.Application.Messaging.Agent.Tools;

/// <summary>
/// <c>creer_lien_devis</c>: for an option of the latest search (by its id — never free amounts), a quote locked for 24
/// hours (<see cref="IBookingRequests.CreateLockedQuoteAsync"/>, priced again by the engine) and the deep link of the
/// booking funnel that loads it (<c>devis=CODE&amp;src=whatsapp</c>). It creates no booking: the customer finalises the
/// request on the site and an agent validates it.
/// </summary>
internal sealed class QuoteLinkTool(IBookingRequests requests, IOptions<AgentSiteOptions> site) : IAgentTool
{
    public const string ToolName = "creer_lien_devis";

    public string Name => ToolName;

    public string Description =>
        "Crée un devis verrouillé 24 heures pour une option renvoyée par rechercher_disponibilites (par son id) et renvoie le lien "
        + "sécurisé du site où le client finalise sa demande (conducteurs, documents, paiement). Ne crée aucune réservation : "
        + "un agent valide ensuite la demande.";

    public string InputSchema => """
        {"type":"object","properties":{"option_id":{"type":"string","maxLength":10,"description":"Id d'une option de la dernière recherche, par exemple A1."}},"required":["option_id"],"additionalProperties":false}
        """;

    public async Task<AgentToolOutcome> RunAsync(ToolArguments arguments, AgentTurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        var option = context.State.FindOption(arguments.String("option_id"));
        if (option is null || option.AgencyId != context.Turn.AgencyId)
        {
            return AgentToolOutcome.Error(
                "Option inconnue ou périmée : utilise un id de la dernière recherche, ou relance rechercher_disponibilites.",
                new { options_valides = context.State.Options.Select(o => o.Id).ToList() });
        }

        if (option.PickupAt <= context.Now)
        {
            return AgentToolOutcome.Error("Le départ de cette option est passé : relance rechercher_disponibilites avec de nouvelles dates.");
        }

        if (site.Value.PublicBaseUrl is not { IsAbsoluteUri: true } baseUrl)
        {
            return AgentToolOutcome.Error("Le lien du site n'est pas configuré : propose de passer la main à un agent.");
        }

        var quote = await requests.CreateLockedQuoteAsync(
            new PriceRequest(option.AgencyId, option.CategoryId, option.PickupLocationId, option.ReturnLocationId, option.PickupAt, option.ReturnAt, SalesChannel.WhatsApp),
            cancellationToken);
        if (quote.IsFailure)
        {
            return AgentToolOutcome.Error(
                "Le devis n'a pas pu être créé : relance rechercher_disponibilites, ou propose de passer la main à un agent.",
                new { code = quote.Error.Code });
        }

        var locked = quote.Value;
        var link = BookingDeepLink.Build(
            baseUrl, context.Language, option.PickupLocationCode, option.ReturnLocationCode, option.PickupAt, option.ReturnAt, option.CategoryCode, locked.Code);
        context.Remember(locked.TotalInclVat, locked.Deposit);
        context.State.RecordQuoteLink(link.AbsoluteUri, context.Now);
        context.QuoteLinkCreated = true;
        context.Progress = true;
        return new AgentToolOutcome(new
        {
            statut = "ok",
            option = option.Id,
            categorie = option.CategoryName,
            lien = link.AbsoluteUri,
            code_devis = locked.Code,
            total_ttc = AgentJson.Amount(locked.TotalInclVat),
            caution = AgentJson.Amount(locked.Deposit),
            prix_garanti_jusqu_au = AgentJson.Local(locked.ExpiresAtUtc) + " (heure de Tunis)",
            rappel = "Le prix est garanti 24 heures avec ce lien. Le client y finalise sa demande ; un agent la valide ensuite, la confirmation arrive par e-mail et WhatsApp.",
        });
    }
}
