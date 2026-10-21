using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;

namespace CarRental.Application.Channels.Adapters;

/// <summary>
/// An adapter translates the format of one channel into the canonical format (docs/conception/phase-5-canaux.md §2,
/// PROMPT §9.3): <see cref="HandleInboundAsync"/> reads a message of the channel (availability, create, modify,
/// cancel, read), lets the core answer it and writes the answer back; <see cref="PushAvailabilityAsync"/> and
/// <see cref="PushRatesAsync"/> send updates; <see cref="MapBooking"/> resolves the channel's codes with the mappings of
/// the agency. No format is invented: an adapter is written from the specification of its channel.
/// </summary>
/// <remarks>Adapters are singletons, stateless (or thread-safe), and never throw for a business failure.</remarks>
public interface IChannelAdapter
{
    ChannelAdapterKind Kind { get; }

    /// <summary>Reads <paramref name="message"/>, runs it through <paramref name="core"/> and formats the answer.</summary>
    Task<ChannelInboundResult> HandleInboundAsync(ChannelInboundMessage message, IChannelInboundCore core, CancellationToken cancellationToken);

    /// <summary>Sends availability; a failure is retried by the sync queue with growing delays.</summary>
    Task<Result> PushAvailabilityAsync(ChannelPushTarget target, ChannelAvailabilityUpdate update, CancellationToken cancellationToken);

    /// <summary>Sends the daily prices of the engine.</summary>
    Task<Result> PushRatesAsync(ChannelPushTarget target, ChannelRatesUpdate update, CancellationToken cancellationToken);

    /// <summary>The internal category, places and extras of a booking described with the channel's codes.</summary>
    Result<MappedReservation> MapBooking(ChannelReservation reservation, ChannelMappingSet mappings);
}

/// <summary>The core of the Channels module, as an adapter calls it: one canonical request, one canonical outcome.</summary>
public interface IChannelInboundCore
{
    Task<ChannelInboundOutcome> HandleAsync(ChannelInboundRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// One inbound HTTP message of a channel: the method and the path after <c>/api/channels/{code}</c>, the query string,
/// the content type and the raw body (already size-limited by the endpoint).
/// </summary>
public sealed record ChannelInboundMessage(string ChannelCode, string Method, string Path, string? Query, string? ContentType, ReadOnlyMemory<byte> Body);

/// <summary>
/// What an adapter answers: the HTTP status, content type and body in the channel's format, plus the canonical request
/// it read (<see langword="null"/> when unreadable) and the outcome of the core, for the message log.
/// </summary>
public sealed record ChannelInboundResult(
    int StatusCode,
    string ContentType,
    ReadOnlyMemory<byte> Body,
    ChannelInboundRequest? Request,
    ChannelInboundOutcome? Outcome,
    Error? FormatError = null);

/// <summary>The internal targets of a channel booking, once its codes are mapped.</summary>
public sealed record MappedReservation(Guid CategoryId, Guid PickupLocationId, Guid ReturnLocationId, IReadOnlyList<SelectedExtra> Extras);

/// <summary>
/// The mappings of a channel, both ways: channel code → internal category, location or extra code, and back (the
/// category's own ACRISS code unless the mapping sells it under another).
/// </summary>
public sealed class ChannelMappingSet
{
    private readonly IReadOnlyList<ChannelMappingEntry> _entries;
    private readonly IReadOnlyDictionary<Guid, string> _categoryAcriss;

    public ChannelMappingSet(IEnumerable<ChannelMappingEntry> entries, IReadOnlyDictionary<Guid, string> categoryAcriss)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(categoryAcriss);
        _entries = [.. entries];
        _categoryAcriss = categoryAcriss;
    }

    public static ChannelMappingSet Empty { get; } = new([], new Dictionary<Guid, string>());

    public IReadOnlyList<ChannelMappingEntry> Entries => _entries;

    /// <summary>The mapped categories, in mapping order.</summary>
    public IEnumerable<ChannelMappingEntry> Categories => _entries.Where(e => e.Kind == ChannelMappingKind.Category && e.InternalId is not null);

    /// <summary>The mapped locations, in mapping order.</summary>
    public IEnumerable<ChannelMappingEntry> Locations => _entries.Where(e => e.Kind == ChannelMappingKind.Location && e.InternalId is not null);

    public Guid? CategoryOf(string? channelCode) => Find(ChannelMappingKind.Category, channelCode)?.InternalId;

    public Guid? LocationOf(string? channelCode) => Find(ChannelMappingKind.Location, channelCode)?.InternalId;

    /// <summary>The extra code of the rate plan for an option of the channel, or <see langword="null"/>.</summary>
    public string? ExtraOf(ChannelOptionKind kind, string? channelCode) => Find(KindOf(kind), channelCode)?.InternalCode;

    public string? CodeOfCategory(Guid categoryId) =>
        _entries.FirstOrDefault(e => e.Kind == ChannelMappingKind.Category && e.InternalId == categoryId)?.ChannelCode;

    public string? CodeOfLocation(Guid locationId) =>
        _entries.FirstOrDefault(e => e.Kind == ChannelMappingKind.Location && e.InternalId == locationId)?.ChannelCode;

    /// <summary>The ACRISS code the category is sold under on the channel.</summary>
    public string AcrissOf(Guid categoryId)
    {
        var mapping = _entries.FirstOrDefault(e => e.Kind == ChannelMappingKind.Category && e.InternalId == categoryId);
        return mapping?.AcrissCode ?? _categoryAcriss.GetValueOrDefault(categoryId) ?? string.Empty;
    }

    private static ChannelMappingKind KindOf(ChannelOptionKind kind) => kind switch
    {
        ChannelOptionKind.Insurance => ChannelMappingKind.Insurance,
        ChannelOptionKind.Fee => ChannelMappingKind.Fee,
        _ => ChannelMappingKind.Extra,
    };

    private ChannelMappingEntry? Find(ChannelMappingKind kind, string? channelCode)
    {
        var code = channelCode?.Trim();
        return string.IsNullOrEmpty(code) ? null : _entries.FirstOrDefault(e => e.Kind == kind && ChannelCodes.SameCode(e.ChannelCode, code));
    }
}

/// <summary>One mapping of a channel (a copy of the domain mapping, safe to share).</summary>
public sealed record ChannelMappingEntry(Guid Id, ChannelMappingKind Kind, Guid? InternalId, string? InternalCode, string ChannelCode, string? AcrissCode);

/// <summary>
/// Default <see cref="IChannelAdapter.MapBooking"/>: every code of the booking must be mapped — an unknown category,
/// location or option is refused with a clear code (<c>channels.mapping.*_unmapped</c>), never guessed.
/// </summary>
public static class ChannelBookingMapper
{
    public static Result<MappedReservation> Map(ChannelReservation reservation, ChannelMappingSet mappings)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(mappings);
        if (mappings.CategoryOf(reservation.CategoryCode) is not { } categoryId)
        {
            return ChannelsErrors.CategoryUnmapped;
        }

        if (mappings.LocationOf(reservation.PickupLocationCode) is not { } pickupId || mappings.LocationOf(reservation.ReturnLocationCode) is not { } returnId)
        {
            return ChannelsErrors.LocationUnmapped;
        }

        var extras = new List<SelectedExtra>();
        foreach (var extra in reservation.Extras ?? [])
        {
            if (extra is null || mappings.ExtraOf(extra.Kind, extra.Code) is not { } code)
            {
                return ChannelsErrors.ExtraUnmapped;
            }

            extras.Add(new SelectedExtra(code, extra.Quantity));
        }

        return new MappedReservation(categoryId, pickupId, returnId, extras);
    }
}
