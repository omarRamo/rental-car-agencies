using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Messaging;

namespace CarRental.Application.Messaging.Agent.Tools;

/// <summary>
/// <c>statut_reservation</c>: the state of a rental file in plain words (dates, places, category — no personal data, no
/// amount, no payment detail), given <b>only</b> when the WhatsApp number of the conversation belongs to its main or
/// second driver or to its payer (persons found by this phone, E.164). An unknown number and a file of somebody else get
/// the same answer, so that the tool tells nothing about the files of others; lookups are limited per conversation.
/// </summary>
internal sealed class BookingStatusTool(IBookingsQueries bookings, ICustomersQueries customers, IFleetQueries fleet) : IAgentTool
{
    public const string ToolName = "statut_reservation";

    /// <summary>States a customer may read as confirmed by the agency (the confirmation guardrail lets the reply say so).</summary>
    public static readonly IReadOnlySet<BookingStatusInfo> ConfirmedStates = new HashSet<BookingStatusInfo>
    {
        BookingStatusInfo.Confirmed, BookingStatusInfo.InProgress, BookingStatusInfo.Returned, BookingStatusInfo.Closed,
    };

    private static readonly object NotFound = new
    {
        statut = "introuvable",
        note = "Aucune réservation ne correspond à cette référence pour ce numéro WhatsApp. Ne donne aucun autre détail ; propose de vérifier la référence ou de passer la main à un agent.",
    };

    public string Name => ToolName;

    public string Description =>
        "Donne l'état d'une réservation existante à partir de sa référence (par exemple LOC-2027-000123), seulement si le numéro "
        + "WhatsApp du client correspond au dossier ; sinon répond que rien n'a été trouvé.";

    public string InputSchema => """
        {"type":"object","properties":{"reference":{"type":"string","maxLength":30,"description":"Référence du dossier donnée par le client, par exemple LOC-2027-000123."}},"required":["reference"],"additionalProperties":false}
        """;

    public async Task<AgentToolOutcome> RunAsync(ToolArguments arguments, AgentTurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.State.TryRecordStatusLookup(context.Settings.Caps.MaxStatusLookups))
        {
            return new AgentToolOutcome(new
            {
                statut = "limite_atteinte",
                note = "Trop de recherches de réservation dans cette conversation : propose de passer la main à un agent.",
            });
        }

        // A reference is unique within an agency only: looked up in the agency the bot answers for.
        var booking = await bookings.FindByNumberAsync(context.Agency.Agency.Id, arguments.String("reference")!, cancellationToken);
        if (booking is null || !await BelongsToAsync(booking, context.Turn.WhatsAppNumber, cancellationToken))
        {
            return new AgentToolOutcome(NotFound);
        }

        var category = context.Agency.Categories.FirstOrDefault(c => c.Id == booking.CategoryId)
                       ?? await fleet.GetCategoryAsync(booking.CategoryId, cancellationToken);
        if (ConfirmedStates.Contains(booking.Status))
        {
            context.StatusOfConfirmedBooking = true;
        }

        context.Progress = true;
        return new AgentToolOutcome(new
        {
            statut = "trouve",
            reference = booking.Number,
            etat = StatusText(booking.Status),
            depart = AgentJson.Local(booking.PickupAt) + " (heure de Tunis)",
            retour = AgentJson.Local(booking.ReturnAt) + " (heure de Tunis)",
            lieu_depart = LocationName(context, booking.PickupLocationId),
            lieu_retour = LocationName(context, booking.ReturnLocationId),
            categorie = category is null ? null : $"{category.Name} ({category.ExampleModel} ou similaire)",
            note = "Pour toute modification ou annulation, passe la main à un agent.",
        });
    }

    /// <summary>Whether the number belongs to a driver of the file or to its payer (persons with this phone; a company payer by its phone).</summary>
    private async Task<bool> BelongsToAsync(BookingSummaryInfo booking, string whatsAppNumber, CancellationToken cancellationToken)
    {
        var persons = (await customers.FindPersonsByPhoneAsync(whatsAppNumber, cancellationToken)).Select(p => p.Id).ToHashSet();
        if ((booking.MainDriverId is { } main && persons.Contains(main)) || (booking.SecondaryDriverId is { } second && persons.Contains(second)))
        {
            return true;
        }

        if (booking.PayerCustomerId is not { } payerId || await customers.GetCustomerAsync(payerId, cancellationToken) is not { } payer)
        {
            return false;
        }

        return (payer.PersonId is { } payerPerson && persons.Contains(payerPerson)) || SameNumber(payer.Phone, whatsAppNumber);
    }

    private static bool SameNumber(string? phone, string whatsAppNumber)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return false;
        }

        var digits = string.Concat(phone.Where(c => char.IsAsciiDigit(c) || c == '+'));
        return WhatsAppNumbers.Normalize(digits) is { IsSuccess: true } normalized
               && WhatsAppNumbers.Normalize(whatsAppNumber) is { IsSuccess: true } number
               && string.Equals(normalized.Value, number.Value, StringComparison.Ordinal);
    }

    private static string? LocationName(AgentTurnContext context, Guid locationId) =>
        context.Agency.Locations.FirstOrDefault(l => l.Id == locationId)?.Name;

    private static string StatusText(BookingStatusInfo status) => status switch
    {
        BookingStatusInfo.Draft => "demande commencée mais pas encore envoyée",
        BookingStatusInfo.PendingValidation => "demande reçue, en cours de validation par un agent",
        BookingStatusInfo.InformationRequested => "en attente d'un complément d'information du client",
        BookingStatusInfo.OptionHeld => "option posée, pas encore confirmée",
        BookingStatusInfo.AwaitingPayment => "validée par l'agence, en attente du paiement",
        BookingStatusInfo.Confirmed => "confirmée par l'agence",
        BookingStatusInfo.InProgress => "location en cours",
        BookingStatusInfo.Returned => "véhicule rendu",
        BookingStatusInfo.Closed => "dossier clôturé",
        BookingStatusInfo.Refused => "demande non retenue",
        BookingStatusInfo.Expired => "demande expirée",
        BookingStatusInfo.Cancelled => "annulée",
        BookingStatusInfo.NoShow => "départ non effectué (client absent)",
        _ => "inconnu",
    };
}
