using CarRental.Domain.Common;

namespace CarRental.Domain.Reporting;

/// <summary>
/// One step reached by a visitor of the public booking funnel of an agency, for the conversion indicators of phase 3
/// (searches → requests → confirmed bookings, per channel and source). Append-only measurement data without any personal
/// data: the visit is an anonymous random identifier carried by a first-party cookie, never linked to a person.
/// </summary>
/// <remarks>
/// Rows are written by the funnel recorder and only ever deleted by the retention purge
/// (<c>reporting.funnel_event_retention_days</c>).
/// </remarks>
public sealed class FunnelEvent : Entity, IAgencyOwned
{
    public const int CultureMaxLength = 8;
    public const int SourceMaxLength = 32;
    public const int UtmMaxLength = 100;

    private FunnelEvent()
    {
    }

    private FunnelEvent(Guid id, Guid agencyId, FunnelEventDetails details, DateTimeOffset occurredAtUtc)
        : base(id)
    {
        AgencyId = agencyId;
        VisitId = details.VisitId;
        Step = details.Step;
        OccurredAtUtc = occurredAtUtc;
        Culture = details.Culture;
        Channel = details.Channel;
        Source = details.Source;
        UtmSource = details.UtmSource;
        UtmMedium = details.UtmMedium;
        UtmCampaign = details.UtmCampaign;
        UtmTerm = details.UtmTerm;
        UtmContent = details.UtmContent;
        CategoryCode = details.CategoryCode;
        BookingId = details.BookingId;
    }

    /// <summary>The agency whose public site the visitor browses (ADR-0067).</summary>
    public Guid AgencyId { get; private set; }

    /// <summary>Anonymous visit (first-party cookie), <see langword="null"/> when the visitor opted out (GPC, DNT).</summary>
    public Guid? VisitId { get; private set; }

    public FunnelStep Step { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>UI culture of the page (<c>fr</c>, <c>en</c>).</summary>
    public string Culture { get; private set; } = null!;

    /// <summary>Sales channel the step is priced for: <see cref="SalesChannel.Web"/>, or <see cref="SalesChannel.WhatsApp"/> for a link sent by the bot.</summary>
    public SalesChannel Channel { get; private set; }

    /// <summary>Normalized <c>src</c> parameter of the deep link (lower case), e.g. <c>whatsapp</c>.</summary>
    public string? Source { get; private set; }

    public string? UtmSource { get; private set; }

    public string? UtmMedium { get; private set; }

    public string? UtmCampaign { get; private set; }

    public string? UtmTerm { get; private set; }

    public string? UtmContent { get; private set; }

    /// <summary>Category concerned (requested, highlighted or chosen), when there is one.</summary>
    public AcrissCode? CategoryCode { get; private set; }

    /// <summary>Booking of the funnel once it exists (draft created at the choice of a category).</summary>
    public Guid? BookingId { get; private set; }

    /// <summary>Validates and normalizes a funnel step reached at <paramref name="occurredAt"/> on the public site of <paramref name="agencyId"/>.</summary>
    public static Result<FunnelEvent> Record(Guid agencyId, FunnelEventDetails details, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (agencyId == Guid.Empty)
        {
            return ReportingErrors.FunnelAgencyRequired;
        }

        var normalized = details.Normalize();
        var validation = Validate(normalized);
        return validation.IsFailure
            ? validation.Error
            : new FunnelEvent(NewId(), agencyId, normalized, occurredAt.ToUniversalTime());
    }

    private static Result Validate(FunnelEventDetails details)
    {
        if (!Enum.IsDefined(details.Step))
        {
            return ReportingErrors.FunnelStepInvalid;
        }

        if (!Enum.IsDefined(details.Channel))
        {
            return ReportingErrors.FunnelChannelInvalid;
        }

        if (details.Culture.Length is < 2 or > CultureMaxLength || !details.Culture.All(c => c is (>= 'a' and <= 'z') or '-'))
        {
            return ReportingErrors.FunnelCultureInvalid;
        }

        if (details.VisitId == Guid.Empty || details.BookingId == Guid.Empty)
        {
            return ReportingErrors.FunnelIdentifierInvalid;
        }

        if (details.Source is { } source && (source.Length > SourceMaxLength || !IsSourceText(source)))
        {
            return ReportingErrors.FunnelSourceInvalid;
        }

        string?[] utm = [details.UtmSource, details.UtmMedium, details.UtmCampaign, details.UtmTerm, details.UtmContent];
        return utm.Any(tag => tag is { } value && (value.Length > UtmMaxLength || value.Any(char.IsControl)))
            ? ReportingErrors.FunnelUtmInvalid
            : Result.Success();
    }

    /// <summary>Lower-case letters, digits and <c>. _ -</c>, starting with a letter or a digit.</summary>
    private static bool IsSourceText(string source) =>
        source.Length > 0
        && char.IsAsciiLetterOrDigit(source[0])
        && source.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-');
}

/// <summary>What a funnel step records; text values are trimmed, blank ones dropped, the source and culture lower-cased.</summary>
public sealed record FunnelEventDetails(
    Guid? VisitId,
    FunnelStep Step,
    string Culture,
    SalesChannel Channel,
    string? Source = null,
    string? UtmSource = null,
    string? UtmMedium = null,
    string? UtmCampaign = null,
    string? UtmTerm = null,
    string? UtmContent = null,
    AcrissCode? CategoryCode = null,
    Guid? BookingId = null)
{
    internal FunnelEventDetails Normalize() => this with
    {
        Culture = Culture?.Trim().ToLowerInvariant() ?? string.Empty,
        Source = NullIfBlank(Source)?.ToLowerInvariant(),
        UtmSource = NullIfBlank(UtmSource),
        UtmMedium = NullIfBlank(UtmMedium),
        UtmCampaign = NullIfBlank(UtmCampaign),
        UtmTerm = NullIfBlank(UtmTerm),
        UtmContent = NullIfBlank(UtmContent),
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
