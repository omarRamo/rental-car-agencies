using System.ComponentModel.DataAnnotations.Schema;
using CarRental.Domain.Common;

namespace CarRental.Domain.Messaging;

/// <summary>
/// What the customer is looking for, as understood by the bot (PROMPT §9.2 "état structuré du besoin"): every field is
/// optional and filled turn after turn by the auto-responder (package 4B); the agent sees it in the shared inbox.
/// </summary>
// [ComplexType] (BCL annotation): a value stored in the columns of the conversation, never a table of its own.
[ComplexType]
public sealed record ConversationNeed
{
    public const int PlaceMaxLength = 100;
    public const int MaxPassengers = 50;
    public const int MaxLuggage = 50;

    private ConversationNeed(
        string? pickupPlace,
        string? returnPlace,
        DateTimeOffset? pickupAt,
        DateTimeOffset? returnAt,
        int? passengers,
        int? luggage,
        GearboxPreference? gearbox,
        Money? budget)
    {
        PickupPlace = pickupPlace;
        ReturnPlace = returnPlace;
        PickupAt = pickupAt;
        ReturnAt = returnAt;
        Passengers = passengers;
        Luggage = luggage;
        Gearbox = gearbox;
        Budget = budget;
    }

    /// <summary>Pick-up place: a location code of the platform (<c>TUN</c>) or the words of the customer.</summary>
    public string? PickupPlace { get; private init; }

    /// <summary>Return place, when it differs or is known.</summary>
    public string? ReturnPlace { get; private init; }

    /// <summary>Wished pick-up instant (UTC).</summary>
    public DateTimeOffset? PickupAt { get; private init; }

    /// <summary>Wished return instant (UTC).</summary>
    public DateTimeOffset? ReturnAt { get; private init; }

    public int? Passengers { get; private init; }

    /// <summary>Number of bags or suitcases.</summary>
    public int? Luggage { get; private init; }

    public GearboxPreference? Gearbox { get; private init; }

    /// <summary>Budget for the stay stated by the customer, in TND.</summary>
    public Money? Budget { get; private init; }

    /// <summary>Nothing known yet.</summary>
    public static ConversationNeed Empty { get; } = new(null, null, null, null, null, null, null, null);

    public bool IsEmpty => this == Empty;

    public static Result<ConversationNeed> Create(
        string? pickupPlace = null,
        string? returnPlace = null,
        DateTimeOffset? pickupAt = null,
        DateTimeOffset? returnAt = null,
        int? passengers = null,
        int? luggage = null,
        GearboxPreference? gearbox = null,
        Money? budget = null)
    {
        var pickup = MessageTexts.SingleLine(pickupPlace);
        var dropOff = MessageTexts.SingleLine(returnPlace);
        if (pickup?.Length > PlaceMaxLength
            || dropOff?.Length > PlaceMaxLength
            || (pickupAt is { } start && returnAt is { } end && end <= start)
            || passengers is < 1 or > MaxPassengers
            || luggage is < 0 or > MaxLuggage
            || (gearbox is { } wished && !Enum.IsDefined(wished))
            || (budget is { } amount && (amount.IsNegative || amount.Currency != Currency.Tnd)))
        {
            return MessagingErrors.NeedInvalid;
        }

        return new ConversationNeed(pickup, dropOff, pickupAt?.ToUniversalTime(), returnAt?.ToUniversalTime(), passengers, luggage, gearbox, budget);
    }
}
