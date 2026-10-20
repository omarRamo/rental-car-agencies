using CarRental.Domain.Common;

namespace CarRental.Domain.Fleet;

/// <summary>An other cost of a vehicle (registration fees, accessories, GPS tracker…), VAT included.</summary>
public sealed class VehicleCost : Entity
{
    public const int LabelMaxLength = 120;

    private VehicleCost()
    {
    }

    private VehicleCost(Guid id, Guid vehicleId, DateOnly incurredOn, string label, Money amount)
        : base(id)
    {
        VehicleId = vehicleId;
        IncurredOn = incurredOn;
        Label = label;
        Amount = amount;
    }

    public Guid VehicleId { get; private set; }

    public DateOnly IncurredOn { get; private set; }

    public string Label { get; private set; } = null!;

    public Money Amount { get; private set; }

    internal static Result<VehicleCost> Create(Guid vehicleId, DateOnly incurredOn, string label, Money amount)
    {
        var trimmed = label?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > LabelMaxLength || !FleetAmounts.IsValid(amount) || !amount.IsPositive)
        {
            return FleetErrors.CostLabelInvalid;
        }

        return new VehicleCost(NewId(), vehicleId, incurredOn, trimmed, amount);
    }
}

/// <summary>A photo of a vehicle, stored in the private fleet container and served to back office users only.</summary>
public sealed class VehiclePhoto : Entity
{
    private VehiclePhoto()
    {
    }

    private VehiclePhoto(Guid id, Guid vehicleId, StoredPhoto photo, DateTimeOffset uploadedAt)
        : base(id)
    {
        VehicleId = vehicleId;
        StoragePath = photo.Path;
        ContentType = photo.ContentType;
        ContentHash = photo.ContentHash;
        Length = photo.Length;
        UploadedAtUtc = uploadedAt;
    }

    public Guid VehicleId { get; private set; }

    public string StoragePath { get; private set; } = null!;

    public string ContentType { get; private set; } = null!;

    public string ContentHash { get; private set; } = null!;

    public long Length { get; private set; }

    public DateTimeOffset UploadedAtUtc { get; private set; }

    internal static VehiclePhoto Create(Guid id, Guid vehicleId, StoredPhoto photo, DateTimeOffset uploadedAt) =>
        new(id, vehicleId, photo, uploadedAt);
}

/// <summary>
/// A line of the readable history of a vehicle. <see cref="Subject"/> names what changed (a document or maintenance
/// type, a label), <see cref="PreviousValue"/> and <see cref="NewValue"/> hold invariant values (enum names, ISO dates,
/// kilometres, amounts) that the back office formats in the user's language.
/// </summary>
public sealed class VehicleHistoryEntry : Entity
{
    public const int SubjectMaxLength = 120;
    public const int ValueMaxLength = 200;

    private VehicleHistoryEntry()
    {
    }

    private VehicleHistoryEntry(Guid id, Guid vehicleId, int sequence, VehicleHistoryKind kind, ChangeStamp stamp)
        : base(id)
    {
        VehicleId = vehicleId;
        Sequence = sequence;
        Kind = kind;
        OccurredAtUtc = stamp.At;
        Actor = stamp.By;
    }

    public Guid VehicleId { get; private set; }

    /// <summary>Position in the history of the vehicle (1, 2, 3…): the lines of one change share the same instant.</summary>
    public int Sequence { get; private set; }

    public VehicleHistoryKind Kind { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string? Actor { get; private set; }

    public string? Subject { get; private set; }

    public string? PreviousValue { get; private set; }

    public string? NewValue { get; private set; }

    public string? Reason { get; private set; }

    internal static VehicleHistoryEntry Create(
        Guid vehicleId,
        int sequence,
        VehicleHistoryKind kind,
        ChangeStamp stamp,
        string? subject,
        string? previousValue,
        string? newValue,
        string? reason) =>
        new(NewId(), vehicleId, sequence, kind, stamp)
        {
            Subject = Truncate(subject, SubjectMaxLength),
            PreviousValue = Truncate(previousValue, ValueMaxLength),
            NewValue = Truncate(newValue, ValueMaxLength),
            Reason = Truncate(reason, Vehicle.ReasonMaxLength),
        };

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>
/// An alert sent for a document deadline at a given level (30, 15, 7 days, expired). Its existence makes the daily job
/// idempotent: each (document, level) is notified once, and a renewal (a new document) starts afresh.
/// </summary>
public sealed class DeadlineAlert : Entity
{
    private DeadlineAlert()
    {
    }

    private DeadlineAlert(Guid id, VehicleDocument document, Guid agencyId, DeadlineAlertLevel level, DateTimeOffset sentAt, int recipientCount)
        : base(id)
    {
        DocumentId = document.Id;
        VehicleId = document.VehicleId;
        AgencyId = agencyId;
        DocumentType = document.Type;
        ExpiresOn = document.ExpiresOn ?? throw new ArgumentException("Only documents with an expiry date are alerted.", nameof(document));
        Level = level;
        SentAtUtc = sentAt;
        RecipientCount = recipientCount;
    }

    public Guid DocumentId { get; private set; }

    public Guid VehicleId { get; private set; }

    public Guid AgencyId { get; private set; }

    public VehicleDocumentType DocumentType { get; private set; }

    public DateOnly ExpiresOn { get; private set; }

    public DeadlineAlertLevel Level { get; private set; }

    public DateTimeOffset SentAtUtc { get; private set; }

    public int RecipientCount { get; private set; }

    public static DeadlineAlert Record(VehicleDocument document, Guid agencyId, DeadlineAlertLevel level, DateTimeOffset sentAt, int recipientCount)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegative(recipientCount);
        return new DeadlineAlert(NewId(), document, agencyId, level, sentAt.ToUniversalTime(), recipientCount);
    }

    /// <summary>
    /// Level to notify for a document expiring on <paramref name="expiresOn"/>, given the levels already sent for it:
    /// the current level, unless it (or a more urgent one) was already sent. Levels skipped while the job did not run
    /// are not sent afterwards.
    /// </summary>
    public static DeadlineAlertLevel? LevelToSend(DateOnly expiresOn, DateOnly today, IEnumerable<DeadlineAlertLevel> alreadySent)
    {
        ArgumentNullException.ThrowIfNull(alreadySent);
        if (VehicleCompliance.LevelOf(expiresOn, today) is not { } level)
        {
            return null;
        }

        return alreadySent.Any(sent => sent <= level) ? null : level;
    }
}
