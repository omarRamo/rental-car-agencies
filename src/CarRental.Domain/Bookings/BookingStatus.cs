using CarRental.Domain.Bookings.Availability;

namespace CarRental.Domain.Bookings;

/// <summary>
/// Status of a rental file (docs/conception/phase-1-exploitation.md §2, authoritative). Values are stored: never
/// renumber them.
/// </summary>
public enum BookingStatus
{
    /// <summary>Brouillon: started by the public funnel, not submitted; occupies nothing.</summary>
    Draft = 1,

    /// <summary>En attente de validation: a web request holding capacity until <c>HoldExpiresAt</c>.</summary>
    PendingValidation = 2,

    /// <summary>Complément demandé: the agent asked the customer for more information; the hold is extended.</summary>
    InformationRequested = 3,

    /// <summary>Option datée: a phone option holding capacity until <c>OptionExpiresAt</c>.</summary>
    OptionHeld = 4,

    /// <summary>Validée, en attente de paiement (fallback payment mode): capacity held until <c>PaymentDueAt</c>.</summary>
    AwaitingPayment = 5,

    /// <summary>Confirmée.</summary>
    Confirmed = 6,

    /// <summary>En cours: the vehicle is out.</summary>
    InProgress = 7,

    /// <summary>Restituée: the vehicle is back; the preparation buffer still applies.</summary>
    Returned = 8,

    /// <summary>Clôturée (terminal): settled; keeps the buffer after the actual return, like <see cref="Returned"/>.</summary>
    Closed = 9,

    /// <summary>Refusée (terminal).</summary>
    Refused = 10,

    /// <summary>Expirée (terminal): a hold, an option or a payment deadline passed.</summary>
    Expired = 11,

    /// <summary>Annulée (terminal).</summary>
    Cancelled = 12,

    /// <summary>Non présenté (terminal).</summary>
    NoShow = 13,
}

/// <summary>Classification of the statuses (capacity, terminal, open) used everywhere a status is interpreted.</summary>
public static class BookingStatusRules
{
    /// <summary>Statuses that consume capacity or keep a vehicle busy (every <see cref="OccupancyKind"/> but <c>None</c>).</summary>
    public static IReadOnlyList<BookingStatus> Occupying { get; } =
    [
        BookingStatus.PendingValidation,
        BookingStatus.InformationRequested,
        BookingStatus.OptionHeld,
        BookingStatus.AwaitingPayment,
        BookingStatus.Confirmed,
        BookingStatus.InProgress,
        BookingStatus.Returned,
        BookingStatus.Closed,
    ];

    /// <summary>Statuses whose vehicle is not out yet: <c>[PickupAt, ReturnAt + buffer)</c>.</summary>
    public static IReadOnlyList<BookingStatus> Planned { get; } =
    [
        BookingStatus.PendingValidation,
        BookingStatus.InformationRequested,
        BookingStatus.OptionHeld,
        BookingStatus.AwaitingPayment,
        BookingStatus.Confirmed,
    ];

    /// <summary>
    /// Open files: a request, an option or a rental not settled yet (every status but <c>Draft</c> and the terminal
    /// ones). A person with an open file keeps the images of their documents (retention).
    /// </summary>
    public static IReadOnlyList<BookingStatus> Open { get; } =
    [
        BookingStatus.PendingValidation,
        BookingStatus.InformationRequested,
        BookingStatus.OptionHeld,
        BookingStatus.AwaitingPayment,
        BookingStatus.Confirmed,
        BookingStatus.InProgress,
        BookingStatus.Returned,
    ];

    /// <summary>Web requests waiting for an agent: the request queue.</summary>
    public static IReadOnlyList<BookingStatus> AwaitingDecision { get; } =
    [
        BookingStatus.PendingValidation,
        BookingStatus.InformationRequested,
    ];

    public static bool IsTerminal(this BookingStatus status) =>
        status is BookingStatus.Closed or BookingStatus.Refused or BookingStatus.Expired or BookingStatus.Cancelled or BookingStatus.NoShow;

    public static bool IsOpen(this BookingStatus status) => !status.IsTerminal() && status != BookingStatus.Draft;

    /// <summary>
    /// How the status occupies capacity (ADR-0025): Draft and the terminal statuses without rental occupy nothing; the
    /// statuses before the hand-over are planned; <c>Closed</c> is measured like <c>Returned</c>, from its actual return.
    /// </summary>
    public static OccupancyKind ToOccupancyKind(this BookingStatus status) => status switch
    {
        BookingStatus.PendingValidation
            or BookingStatus.InformationRequested
            or BookingStatus.OptionHeld
            or BookingStatus.AwaitingPayment
            or BookingStatus.Confirmed => OccupancyKind.Planned,
        BookingStatus.InProgress => OccupancyKind.InProgress,
        BookingStatus.Returned or BookingStatus.Closed => OccupancyKind.Returned,
        _ => OccupancyKind.None,
    };
}
