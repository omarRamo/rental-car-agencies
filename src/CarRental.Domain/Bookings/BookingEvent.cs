using System.Text.Json;
using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings;

/// <summary>Kind of a timeline row. Values are stored: never renumber them.</summary>
public enum BookingEventType
{
    Created = 1,
    Submitted = 2,
    InformationRequested = 3,
    InformationProvided = 4,
    Validated = 5,
    Refused = 6,
    Expired = 7,
    OptionConfirmed = 8,
    Paid = 9,
    Cancelled = 10,
    NoShow = 11,
    RentalStarted = 12,
    Returned = 13,
    Closed = 14,
    Amended = 15,
    Extended = 16,
    VehicleAssigned = 17,
    VehicleUnassigned = 18,
    VehicleSwapped = 19,
    Escalated = 20,
}

/// <summary>
/// One row of the business timeline of a rental file (SPEC §14 "chronologie de chaque dossier"): what happened, when
/// (UTC), the status before and after, who did it (a user, a customer, a channel or <c>system</c>), through which
/// channel, the reason when one is required, and a few facts as JSON. Rows are only ever added, numbered per file.
/// </summary>
public sealed class BookingEvent : Entity, IAgencyOwned
{
    public const int DataMaxLength = 2000;

    private BookingEvent()
    {
    }

    private BookingEvent(
        Guid bookingId,
        Guid agencyId,
        int sequence,
        DateTimeOffset occurredAtUtc,
        BookingEventType type,
        BookingStatus? statusBefore,
        BookingStatus statusAfter,
        BookingActor actor,
        string? reason,
        string? data)
        : base(NewId())
    {
        BookingId = bookingId;
        AgencyId = agencyId;
        Sequence = sequence;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
        Type = type;
        StatusBefore = statusBefore;
        StatusAfter = statusAfter;
        ActorUserId = actor.UserId;
        ActorName = actor.Name;
        Channel = actor.Channel;
        Reason = reason;
        Data = data;
    }

    public Guid BookingId { get; private set; }

    /// <summary>The agency of the file (given by the file).</summary>
    public Guid AgencyId { get; private set; }

    /// <summary>Position in the timeline of the file, from 1: the timeline is ordered by it, not by the clock.</summary>
    public int Sequence { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public BookingEventType Type { get; private set; }

    /// <summary>Status before the command; <see langword="null"/> for the creation.</summary>
    public BookingStatus? StatusBefore { get; private set; }

    public BookingStatus StatusAfter { get; private set; }

    public Guid? ActorUserId { get; private set; }

    /// <summary>User name, or a system actor such as <c>system:bookings-expire</c>.</summary>
    public string ActorName { get; private set; } = null!;

    /// <summary>Channel the command came through; <see langword="null"/> for the back office and the platform.</summary>
    public SalesChannel? Channel { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>Facts of the event as a flat JSON object of strings (invariant formats), e.g. the vehicle or the new dates.</summary>
    public string? Data { get; private set; }

    /// <summary>The facts of <see cref="Data"/> (empty when none).</summary>
    public IReadOnlyDictionary<string, string> Facts =>
        Data is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(Data) ?? [];

    internal static BookingEvent Create(
        Guid bookingId,
        Guid agencyId,
        int sequence,
        DateTimeOffset occurredAtUtc,
        BookingEventType type,
        BookingStatus? statusBefore,
        BookingStatus statusAfter,
        BookingActor actor,
        string? reason,
        IReadOnlyDictionary<string, string>? facts)
    {
        var data = facts is { Count: > 0 } ? JsonSerializer.Serialize(facts) : null;
        if (data is { Length: > DataMaxLength })
        {
            throw new ArgumentException($"The facts of a timeline row are limited to {DataMaxLength} characters.", nameof(facts));
        }

        return new BookingEvent(bookingId, agencyId, sequence, occurredAtUtc, type, statusBefore, statusAfter, actor, reason, data);
    }
}
