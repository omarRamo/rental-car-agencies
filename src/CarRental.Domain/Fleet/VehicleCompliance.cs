using CarRental.Domain.Common;

namespace CarRental.Domain.Fleet;

/// <summary>
/// Compliance of a vehicle with its mandatory documents (insurance, road tax, technical inspection): it may be rented
/// until the end of the Tunis day of the earliest expiry; a missing mandatory document means no compliance at all.
/// </summary>
public static class VehicleCompliance
{
    /// <summary>The documents a vehicle needs to be rented (PROMPT §6, invariant 3).</summary>
    public static IReadOnlyList<VehicleDocumentType> MandatoryTypes { get; } =
    [
        VehicleDocumentType.Insurance,
        VehicleDocumentType.RoadTax,
        VehicleDocumentType.TechnicalInspection,
    ];

    public static bool IsMandatory(VehicleDocumentType type) => type is not VehicleDocumentType.RegistrationCard;

    /// <summary>First instant (UTC) after the Tunis calendar day <paramref name="date"/>: a document expiring on that date is valid all day.</summary>
    public static DateTimeOffset EndOfTunisDay(DateOnly date) => TunisTime.StartOfDay(date.AddDays(1));

    /// <summary>
    /// End (exclusive) of the period covered by every mandatory document, or <see langword="null"/> when one of them is
    /// missing or has no expiry date. <paramref name="currentDocuments"/> are the current documents (one per type).
    /// </summary>
    public static DateTimeOffset? CompliantUntil(IEnumerable<(VehicleDocumentType Type, DateOnly? ExpiresOn)> currentDocuments)
    {
        ArgumentNullException.ThrowIfNull(currentDocuments);

        DateOnly? earliest = null;
        var covered = new HashSet<VehicleDocumentType>();
        foreach (var (type, expiresOn) in currentDocuments)
        {
            if (!IsMandatory(type) || expiresOn is not { } expiry)
            {
                continue;
            }

            covered.Add(type);
            if (earliest is null || expiry < earliest)
            {
                earliest = expiry;
            }
        }

        return earliest is { } date && MandatoryTypes.All(covered.Contains) ? EndOfTunisDay(date) : null;
    }

    /// <summary>
    /// Urgency of a deadline on <paramref name="today"/> (Tunis date): <see langword="null"/> when the expiry is more than
    /// 30 days away (or unknown); a document expiring today is still valid, hence "within 7 days".
    /// </summary>
    public static DeadlineAlertLevel? LevelOf(DateOnly? expiresOn, DateOnly today)
    {
        if (expiresOn is not { } expiry)
        {
            return null;
        }

        var daysLeft = expiry.DayNumber - today.DayNumber;
        return daysLeft switch
        {
            < 0 => DeadlineAlertLevel.Expired,
            <= 7 => DeadlineAlertLevel.Within7Days,
            <= 15 => DeadlineAlertLevel.Within15Days,
            <= 30 => DeadlineAlertLevel.Within30Days,
            _ => null,
        };
    }
}
