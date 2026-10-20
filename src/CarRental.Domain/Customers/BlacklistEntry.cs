using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>
/// A blacklist decision about a person: mandatory reason, author and date; lifted later with its own reason, author
/// and date. Never shown to the person (the booking funnel applies it silently, SPEC §5). Part of <see cref="Person"/>.
/// </summary>
public sealed class BlacklistEntry : Entity
{
    public const int ReasonMinLength = 3;
    public const int ReasonMaxLength = 500;
    public const int AuthorNameMaxLength = AuditActor.UserNameMaxLength;

    private BlacklistEntry()
    {
    }

    private BlacklistEntry(Guid id, Guid personId, string reason, AuditActor author, DateTimeOffset now)
        : base(id)
    {
        PersonId = personId;
        Reason = reason;
        AddedAtUtc = now;
        AddedByUserId = author.UserId;
        AddedByName = Truncate(author.UserName);
    }

    public Guid PersonId { get; private set; }

    public string Reason { get; private set; } = null!;

    public DateTimeOffset AddedAtUtc { get; private set; }

    public Guid? AddedByUserId { get; private set; }

    public string? AddedByName { get; private set; }

    public DateTimeOffset? LiftedAtUtc { get; private set; }

    public Guid? LiftedByUserId { get; private set; }

    public string? LiftedByName { get; private set; }

    public string? LiftReason { get; private set; }

    public bool IsActive => LiftedAtUtc is null;

    /// <summary>Trimmed reason, or <see langword="null"/> when it is too short or too long.</summary>
    internal static string? CleanReason(string? reason) =>
        TextRules.Clean(reason) is { Length: >= ReasonMinLength and <= ReasonMaxLength } cleaned ? cleaned : null;

    internal static BlacklistEntry Add(Guid personId, string reason, AuditActor author, DateTimeOffset now) =>
        new(NewId(), personId, reason, author, now);

    internal void Lift(string reason, AuditActor author, DateTimeOffset now)
    {
        LiftedAtUtc = now;
        LiftedByUserId = author.UserId;
        LiftedByName = Truncate(author.UserName);
        LiftReason = reason;
    }

    private static string? Truncate(string? name) => name is { Length: > AuthorNameMaxLength } ? name[..AuthorNameMaxLength] : name;
}
