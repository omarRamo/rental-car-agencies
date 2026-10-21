using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Access;

/// <summary>Steps of the booking funnel measured once per session (funnel events).</summary>
[Flags]
public enum FunnelSteps
{
    None = 0,
    Options = 1,
    Documents = 2,
    Payment = 4,
    Confirmation = 8,
}

/// <summary>What starts a funnel session: the validated search, the chosen category and its locked quote.</summary>
public sealed record FunnelStart(
    Guid AgencyId,
    string Culture,
    SalesChannel Channel,
    BookingAttribution Attribution,
    Guid CategoryId,
    string CategoryCode,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    string SearchQuery,
    Guid QuoteId,
    string QuoteCode,
    DateTimeOffset QuoteExpiresAtUtc,
    IReadOnlyList<ExtraChoice> Extras);

/// <summary>
/// The booking funnel of a visitor (ADR-0049): opened when a category is chosen, identified by a random token carried
/// by the addresses of the steps (only its hash is stored), it holds the culture, the search, the category, the current
/// locked quote (whose price it guarantees, and with which it expires), the attribution, the chosen extras, the
/// drivers as typed and the consents. No booking exists before the submission, and no capacity is taken before it.
/// </summary>
/// <remarks>
/// The drivers are kept as an opaque JSON document written by the application (personal data: masked in the audit
/// log, deleted with the session by the purge job). The token is never stored: an unknown or expired token leads back
/// to the search.
/// </remarks>
public sealed class FunnelSession : AggregateRoot
{
    public const int CultureMaxLength = 5;
    public const int CategoryCodeLength = 4;
    public const int SearchQueryMaxLength = 800;
    public const int DriversMaxLength = 16_000;
    public const int NoticeMaxLength = 64;

    private FunnelSession()
    {
    }

    private FunnelSession(byte[] tokenHash, FunnelStart start, DateTimeOffset now)
        : base(NewId())
    {
        TokenHash = tokenHash;
        AgencyId = start.AgencyId;
        Culture = start.Culture;
        Channel = start.Channel;
        Attribution = start.Attribution;
        CategoryId = start.CategoryId;
        CategoryCode = start.CategoryCode;
        PickupLocationId = start.PickupLocationId;
        ReturnLocationId = start.ReturnLocationId;
        PickupAt = start.PickupAt.ToUniversalTime();
        ReturnAt = start.ReturnAt.ToUniversalTime();
        SearchQuery = start.SearchQuery;
        QuoteId = start.QuoteId;
        QuoteCode = start.QuoteCode;
        ExpiresAtUtc = start.QuoteExpiresAtUtc.ToUniversalTime();
        ExtraSelection = ExtraChoice.Format(start.Extras);
        CreatedAtUtc = now.ToUniversalTime();
        LastActivityAtUtc = CreatedAtUtc;
    }

    /// <summary>SHA-256 of the token of the addresses (<see cref="AccessSecrets.HashToken"/>), unique.</summary>
    public byte[] TokenHash { get; private set; } = null!;

    public Guid AgencyId { get; private set; }

    /// <summary>Language of the visitor (<c>fr</c>, <c>en</c>): pages, e-mails and payment page.</summary>
    public string Culture { get; private set; } = null!;

    /// <summary><see cref="SalesChannel.Web"/>, or <see cref="SalesChannel.WhatsApp"/> for a link of the bot (<c>src=whatsapp</c>).</summary>
    public SalesChannel Channel { get; private set; }

    public BookingAttribution Attribution { get; private set; } = BookingAttribution.None;

    public Guid CategoryId { get; private set; }

    public string CategoryCode { get; private set; } = null!;

    public Guid PickupLocationId { get; private set; }

    public Guid ReturnLocationId { get; private set; }

    public DateTimeOffset PickupAt { get; private set; }

    public DateTimeOffset ReturnAt { get; private set; }

    /// <summary>The canonical query of the search (back to the results, a new search after the expiry).</summary>
    public string SearchQuery { get; private set; } = null!;

    /// <summary>The current locked quote: the price shown and guaranteed.</summary>
    public Guid QuoteId { get; private set; }

    public string QuoteCode { get; private set; } = null!;

    /// <summary>End of the price guarantee of the current quote, hence of the session.</summary>
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    /// <summary>The selectable extras chosen, in their stored form (<see cref="ExtraChoice.Format"/>).</summary>
    public string ExtraSelection { get; private set; } = string.Empty;

    /// <summary>The drivers as typed and validated (JSON of the application); <see langword="null"/> before step 2.</summary>
    public string? DriversJson { get; private set; }

    /// <summary>The last form of step 2 sent with errors (values and errors, JSON), shown once on the next display.</summary>
    public string? DraftJson { get; private set; }

    public bool DocumentProcessingConsent { get; private set; }

    public bool WhatsAppNotificationsConsent { get; private set; }

    public bool MarketingConsent { get; private set; }

    /// <summary>A message to show once on the next page (resource key part, e.g. <c>price_updated</c>).</summary>
    public string? PendingNotice { get; private set; }

    public FunnelSteps TrackedSteps { get; private set; }

    /// <summary>The request created by the submission.</summary>
    public Guid? BookingId { get; private set; }

    public string? BookingNumber { get; private set; }

    public DateTimeOffset? SubmittedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset LastActivityAtUtc { get; private set; }

    public IReadOnlyList<ExtraChoice> Extras => ExtraChoice.Parse(ExtraSelection);

    public bool IsSubmitted => BookingId is not null;

    public bool HasDrivers => DriversJson is not null;

    public static Result<FunnelSession> Start(byte[] tokenHash, FunnelStart start, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        ArgumentNullException.ThrowIfNull(start);
        if (tokenHash.Length != AccessSecrets.HashLength)
        {
            throw new ArgumentException("A token hash is a SHA-256.", nameof(tokenHash));
        }

        if (start.AgencyId == Guid.Empty || start.CategoryId == Guid.Empty || start.QuoteId == Guid.Empty
            || start.PickupLocationId == Guid.Empty || start.ReturnLocationId == Guid.Empty)
        {
            throw new ArgumentException("A funnel session needs its agency, category, locations and quote.", nameof(start));
        }

        if (start.ReturnAt <= start.PickupAt)
        {
            return BookingErrors.PeriodInvalid;
        }

        if (start.QuoteExpiresAtUtc <= now)
        {
            return BookingErrors.QuoteExpired;
        }

        if (string.IsNullOrWhiteSpace(start.Culture) || start.Culture.Length > CultureMaxLength
            || start.CategoryCode.Length != CategoryCodeLength || start.SearchQuery.Length > SearchQueryMaxLength)
        {
            throw new ArgumentException("Culture, category code or search query out of bounds.", nameof(start));
        }

        var extras = ExtraChoice.Normalize(start.Extras);
        if (extras.IsFailure)
        {
            return extras.Error;
        }

        return new FunnelSession(tokenHash, start with { Extras = extras.Value }, now);
    }

    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAtUtc <= now;

    /// <summary>
    /// A new locked quote after a change of an input of the price (extras, drivers): the guarantee restarts with it and the
    /// session follows its expiry (ADR-0049). <paramref name="notice"/> is shown once on the next page.
    /// </summary>
    public Result Relock(Guid quoteId, string quoteCode, DateTimeOffset expiresAtUtc, IReadOnlyList<ExtraChoice> extras, string? notice, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quoteCode);
        var open = EnsureOpen(now);
        if (open.IsFailure)
        {
            return open;
        }

        var normalized = ExtraChoice.Normalize(extras);
        if (normalized.IsFailure)
        {
            return normalized.Error;
        }

        if (expiresAtUtc <= now)
        {
            return BookingErrors.QuoteExpired;
        }

        QuoteId = quoteId;
        QuoteCode = quoteCode;
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
        ExtraSelection = ExtraChoice.Format(normalized.Value);
        PendingNotice = Clip(notice);
        Touch(now);
        return Result.Success();
    }

    /// <summary>Records the drivers of step 2 (validated by the application) and the consents; the draft is cleared.</summary>
    public Result SaveDrivers(string driversJson, bool documentProcessing, bool whatsAppNotifications, bool marketing, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driversJson);
        var open = EnsureOpen(now);
        if (open.IsFailure)
        {
            return open;
        }

        if (driversJson.Length > DriversMaxLength)
        {
            throw new ArgumentException("The drivers document is too large.", nameof(driversJson));
        }

        if (!documentProcessing)
        {
            return FunnelErrors.DocumentConsentRequired;
        }

        DriversJson = driversJson;
        DraftJson = null;
        DocumentProcessingConsent = true;
        WhatsAppNotificationsConsent = whatsAppNotifications;
        MarketingConsent = marketing;
        Touch(now);
        return Result.Success();
    }

    /// <summary>Keeps the last form sent with errors (flash), or clears it.</summary>
    public Result SaveDraft(string? draftJson, DateTimeOffset now)
    {
        var open = EnsureOpen(now);
        if (open.IsFailure)
        {
            return open;
        }

        if (draftJson is { Length: > DriversMaxLength })
        {
            throw new ArgumentException("The draft is too large.", nameof(draftJson));
        }

        DraftJson = draftJson;
        Touch(now);
        return Result.Success();
    }

    /// <summary>The notice to show, cleared at once.</summary>
    public string? TakeNotice()
    {
        var notice = PendingNotice;
        PendingNotice = null;
        return notice;
    }

    /// <summary>Whether <paramref name="step"/> is measured for the first time (then it is recorded as measured).</summary>
    public bool MarkTracked(FunnelSteps step)
    {
        if (TrackedSteps.HasFlag(step))
        {
            return false;
        }

        TrackedSteps |= step;
        return true;
    }

    /// <summary>The request was created: the session only leads to its confirmation from now on.</summary>
    public Result MarkSubmitted(Guid bookingId, string bookingNumber, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookingNumber);
        var open = EnsureOpen(now);
        if (open.IsFailure)
        {
            return open;
        }

        BookingId = bookingId;
        BookingNumber = bookingNumber;
        SubmittedAtUtc = now.ToUniversalTime();
        DraftJson = null;
        Touch(now);
        return Result.Success();
    }

    private static string? Clip(string? notice) =>
        string.IsNullOrWhiteSpace(notice) ? null : notice.Length > NoticeMaxLength ? notice[..NoticeMaxLength] : notice;

    private Result EnsureOpen(DateTimeOffset now)
    {
        if (IsSubmitted)
        {
            return FunnelErrors.AlreadySubmitted;
        }

        return IsExpiredAt(now) ? FunnelErrors.SessionExpired : Result.Success();
    }

    private void Touch(DateTimeOffset now) => LastActivityAtUtc = now.ToUniversalTime();
}

/// <summary>Who an uploaded photo is for in the funnel.</summary>
public enum FunnelDriver
{
    Main = 1,
    Second = 2,
}

/// <summary>Which document a photo shows: the identity document (CIN or passport, by nationality), the licence or the international permit.</summary>
public enum FunnelDocument
{
    Identity = 1,
    License = 2,
    Permit = 3,
}

/// <summary>Side of a photographed document.</summary>
public enum FunnelSide
{
    Front = 1,
    Back = 2,
}

/// <summary>A photo slot of the funnel or "Ma réservation": driver, document and side (<c>main.license.back</c>).</summary>
public sealed record UploadSlot(FunnelDriver Driver, FunnelDocument Document, FunnelSide Side)
{
    public const int KeyMaxLength = 24;

    /// <summary>Every slot, in form order.</summary>
    public static IReadOnlyList<UploadSlot> All { get; } =
    [
        .. from driver in Enum.GetValues<FunnelDriver>()
           from document in Enum.GetValues<FunnelDocument>()
           from side in Enum.GetValues<FunnelSide>()
           select new UploadSlot(driver, document, side),
    ];

    /// <summary>Stable key used in forms and stored (lower case).</summary>
    public string Key => $"{Driver.ToString().ToLowerInvariant()}.{Document.ToString().ToLowerInvariant()}.{Side.ToString().ToLowerInvariant()}";

    /// <summary>The slot of a key, or <see langword="null"/> (unknown keys are refused, never stored).</summary>
    public static UploadSlot? Parse(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(slot => string.Equals(slot.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A photo uploaded during a funnel session (insert only: concurrent uploads never conflict); the last one of a slot
/// counts. The file itself is staged by the Customers module, referenced by <see cref="StagedImageId"/>.
/// </summary>
public sealed class FunnelUpload : Entity
{
    public const int ContentTypeMaxLength = 64;

    private FunnelUpload()
    {
    }

    private FunnelUpload(Guid sessionId, UploadSlot slot, Guid stagedImageId, string contentType, long length, DateTimeOffset now)
        : base(NewId())
    {
        SessionId = sessionId;
        Slot = slot.Key;
        StagedImageId = stagedImageId;
        ContentType = contentType;
        Length = length;
        UploadedAtUtc = now.ToUniversalTime();
    }

    public Guid SessionId { get; private set; }

    /// <summary>Key of the <see cref="UploadSlot"/>.</summary>
    public string Slot { get; private set; } = null!;

    public Guid StagedImageId { get; private set; }

    public string ContentType { get; private set; } = null!;

    public long Length { get; private set; }

    public DateTimeOffset UploadedAtUtc { get; private set; }

    public static FunnelUpload Create(Guid sessionId, UploadSlot slot, Guid stagedImageId, string contentType, long length, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (sessionId == Guid.Empty || stagedImageId == Guid.Empty)
        {
            throw new ArgumentException("An upload belongs to a session and a staged image.");
        }

        return new FunnelUpload(sessionId, slot, stagedImageId, contentType.Length > ContentTypeMaxLength ? contentType[..ContentTypeMaxLength] : contentType, length, now);
    }
}
