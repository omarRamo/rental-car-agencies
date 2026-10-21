using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Handover;

/// <summary>
/// The accessories found in the vehicle (SPEC §6 "accessoires"): safety and repair kit, keys, child seats, GPS and the
/// vehicle papers. Compared at the return with the departure.
/// </summary>
// [ComplexType] (BCL annotation): stored in the columns of the inspection, never a table of its own.
[ComplexType]
public sealed record AccessoryChecklist
{
    public const int MaxKeys = 5;
    public const int MaxChildSeats = 4;

    private AccessoryChecklist(
        bool spareWheel,
        bool jack,
        bool warningTriangle,
        bool safetyVest,
        int keys,
        int childSeats,
        bool gps,
        bool vehiclePapers)
    {
        SpareWheel = spareWheel;
        Jack = jack;
        WarningTriangle = warningTriangle;
        SafetyVest = safetyVest;
        Keys = keys;
        ChildSeats = childSeats;
        Gps = gps;
        VehiclePapers = vehiclePapers;
    }

    public bool SpareWheel { get; private init; }

    public bool Jack { get; private init; }

    public bool WarningTriangle { get; private init; }

    public bool SafetyVest { get; private init; }

    public int Keys { get; private init; }

    public int ChildSeats { get; private init; }

    public bool Gps { get; private init; }

    /// <summary>Registration card, insurance certificate and the other papers kept in the vehicle.</summary>
    public bool VehiclePapers { get; private init; }

    public static Result<AccessoryChecklist> Create(
        bool spareWheel,
        bool jack,
        bool warningTriangle,
        bool safetyVest,
        int keys,
        int childSeats,
        bool gps,
        bool vehiclePapers)
    {
        if (keys is < 0 or > MaxKeys || childSeats is < 0 or > MaxChildSeats)
        {
            return HandoverErrors.AccessoriesInvalid;
        }

        return new AccessoryChecklist(spareWheel, jack, warningTriangle, safetyVest, keys, childSeats, gps, vehiclePapers);
    }

    /// <summary>What was in the vehicle at <paramref name="departure"/> and is missing now (fewer keys or seats included).</summary>
    public IReadOnlyList<AccessoryItem> MissingComparedWith(AccessoryChecklist departure)
    {
        ArgumentNullException.ThrowIfNull(departure);
        var missing = new List<AccessoryItem>();
        void Check(bool before, bool now, AccessoryItem item)
        {
            if (before && !now)
            {
                missing.Add(item);
            }
        }

        Check(departure.SpareWheel, SpareWheel, AccessoryItem.SpareWheel);
        Check(departure.Jack, Jack, AccessoryItem.Jack);
        Check(departure.WarningTriangle, WarningTriangle, AccessoryItem.WarningTriangle);
        Check(departure.SafetyVest, SafetyVest, AccessoryItem.SafetyVest);
        Check(departure.Keys > Keys, false, AccessoryItem.Keys);
        Check(departure.ChildSeats > ChildSeats, false, AccessoryItem.ChildSeats);
        Check(departure.Gps, Gps, AccessoryItem.Gps);
        Check(departure.VehiclePapers, VehiclePapers, AccessoryItem.VehiclePapers);
        return missing;
    }
}

/// <summary>An item of the accessory checklist.</summary>
public enum AccessoryItem
{
    SpareWheel = 1,
    Jack = 2,
    WarningTriangle = 3,
    SafetyVest = 4,
    Keys = 5,
    ChildSeats = 6,
    Gps = 7,
    VehiclePapers = 8,
}

/// <summary>
/// The originals a driver showed at the counter at the departure: identity document (CIN or passport) and driving
/// licence. The documents themselves (numbers, validity, images) are kept by the Customers module.
/// </summary>
// [NotMapped] (BCL annotation): a value stored by the inspection in its own text form, never an entity.
[NotMapped]
public sealed record DriverOriginals(Guid PersonId, bool IdentityDocumentSeen, bool DrivingLicenseSeen)
{
    public bool IsComplete => IdentityDocumentSeen && DrivingLicenseSeen;

    /// <summary>Stored form: <c>{personId}:{0|1}{0|1};…</c> in person order.</summary>
    public static string Format(IEnumerable<DriverOriginals> originals)
    {
        ArgumentNullException.ThrowIfNull(originals);
        return string.Join(
            ';',
            originals
                .OrderBy(o => o.PersonId)
                .Select(o => string.Create(CultureInfo.InvariantCulture, $"{o.PersonId:D}:{(o.IdentityDocumentSeen ? 1 : 0)}{(o.DrivingLicenseSeen ? 1 : 0)}")));
    }

    /// <summary>Reads the stored form written by <see cref="Format"/> (invalid parts are ignored).</summary>
    public static IReadOnlyList<DriverOriginals> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        var originals = new List<DriverOriginals>();
        foreach (var part in stored.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.LastIndexOf(':');
            if (separator > 0
                && Guid.TryParse(part.AsSpan(0, separator), out var personId)
                && part.Length - separator - 1 == 2)
            {
                originals.Add(new DriverOriginals(personId, part[separator + 1] == '1', part[separator + 2] == '1'));
            }
        }

        return originals;
    }
}

/// <summary>
/// A file kept in a private blob container for a hand-over (photo, thumbnail, signature, contract): its path in the
/// container, its real content type (checked from its signature), its SHA-256 and its length.
/// </summary>
[NotMapped]
public sealed record HandoverFile
{
    public const int PathMaxLength = 300;
    public const int ContentTypeMaxLength = 100;
    public const int ContentHashLength = 64;

    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string WebP = "image/webp";
    public const string Pdf = "application/pdf";

    private HandoverFile(string path, string contentType, string contentHash, long length)
    {
        Path = path;
        ContentType = contentType;
        ContentHash = contentHash;
        Length = length;
    }

    public string Path { get; }

    public string ContentType { get; }

    /// <summary>Lower-case hexadecimal SHA-256 of the content.</summary>
    public string ContentHash { get; }

    public long Length { get; }

    /// <summary>A stored file; a malformed reference is a programming error (the application builds it).</summary>
    public static HandoverFile Create(string path, string contentType, string contentHash, long length)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > PathMaxLength)
        {
            throw new ArgumentException($"A file path has 1 to {PathMaxLength} characters.", nameof(path));
        }

        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > ContentTypeMaxLength)
        {
            throw new ArgumentException("A content type is required.", nameof(contentType));
        }

        if (contentHash is not { Length: ContentHashLength } || !contentHash.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("A content hash is a lower-case hexadecimal SHA-256.", nameof(contentHash));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return new HandoverFile(path, contentType, contentHash, length);
    }

    public bool IsImage => ContentType is Jpeg or Png or WebP;
}

/// <summary>
/// A manager's traced decision to let a vehicle leave although the amount due is not paid (deferred) or no deposit is
/// held (waived) — e.g. a company paying by invoice (ADR-0041). Recorded on the departure inspection.
/// </summary>
[ComplexType]
public sealed record DepartureDeferral
{
    public DepartureDeferral(Money balanceDeferred, bool depositWaived, string reason, Guid? byUserId, string byName, DateTimeOffset atUtc)
    {
        BalanceDeferred = balanceDeferred;
        DepositWaived = depositWaived;
        Reason = reason;
        ByUserId = byUserId;
        ByName = byName;
        AtUtc = atUtc.ToUniversalTime();
    }

    /// <summary>The amount due when it was deferred (zero when only the deposit is waived); a larger amount due blocks again.</summary>
    public Money BalanceDeferred { get; private init; }

    public bool DepositWaived { get; private init; }

    public string Reason { get; private init; }

    public Guid? ByUserId { get; private init; }

    public string ByName { get; private init; }

    public DateTimeOffset AtUtc { get; private init; }
}

/// <summary>The contract was signed on paper instead of the tablet (accessible fallback, ADR-0041), with the reason.</summary>
[ComplexType]
public sealed record PaperSignature
{
    public PaperSignature(string reason, Guid? byUserId, string byName, DateTimeOffset atUtc)
    {
        Reason = reason;
        ByUserId = byUserId;
        ByName = byName;
        AtUtc = atUtc.ToUniversalTime();
    }

    public string Reason { get; private init; }

    public Guid? ByUserId { get; private init; }

    public string ByName { get; private init; }

    public DateTimeOffset AtUtc { get; private init; }
}

/// <summary>A damage to record or to change: view and position on the schematic (percentages), type, severity, note.</summary>
public sealed record DamageInput(
    DamageView View,
    decimal X,
    decimal Y,
    DamageType Type,
    DamageSeverity Severity,
    string? Note,
    bool IsPreExisting = false);

/// <summary>Rental days and late days (SPEC §7 rule 1: 24 h units from the pick-up hour, one more beyond the tolerance).</summary>
public static class RentalDays
{
    /// <summary>
    /// The rental days of a duration: <c>max(1, ⌊D / 24 h⌋ + (D mod 24 h &gt; tolerance ? 1 : 0))</c> — the rule of the
    /// pricing engine, which remains the only source of prices; this count only tells whether a return is late.
    /// </summary>
    public static int Of(TimeSpan duration, TimeSpan tolerance)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 1;
        }

        var whole = duration.Ticks / TimeSpan.TicksPerDay;
        var rest = duration.Ticks % TimeSpan.TicksPerDay;
        return (int)Math.Max(1, whole + (rest > tolerance.Ticks ? 1 : 0));
    }

    /// <summary>
    /// The days a return at <paramref name="actualReturnAt"/> adds to a rental planned from <paramref name="pickupAt"/> to
    /// <paramref name="plannedReturnAt"/>: zero on time, within the tolerance, or early.
    /// </summary>
    public static int Late(DateTimeOffset pickupAt, DateTimeOffset plannedReturnAt, DateTimeOffset actualReturnAt, TimeSpan tolerance)
    {
        if (actualReturnAt <= plannedReturnAt)
        {
            return 0;
        }

        return Math.Max(0, Of(actualReturnAt - pickupAt, tolerance) - Of(plannedReturnAt - pickupAt, tolerance));
    }
}
