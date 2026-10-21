using System.Text;
using System.Text.Json;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;

namespace CarRental.Application.Channels.Adapters;

/// <summary>
/// The adapters registered in this environment, by kind: the manual adapter always, the test adapter in Development and
/// Testing only, the Booking.com adapter once its format is known (it answers <c>channels.adapter_not_configured</c>
/// until then).
/// </summary>
public sealed class ChannelAdapterRegistry
{
    private readonly Dictionary<ChannelAdapterKind, IChannelAdapter> _adapters;

    public ChannelAdapterRegistry(IEnumerable<IChannelAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = [];
        foreach (var adapter in adapters)
        {
            // The last registration of a kind wins (a test host may replace an adapter).
            _adapters[adapter.Kind] = adapter;
        }
    }

    /// <summary>The kinds a channel can be given here, in their declaration order.</summary>
    public IReadOnlyList<ChannelAdapterKind> AvailableKinds => [.. Enum.GetValues<ChannelAdapterKind>().Where(_adapters.ContainsKey)];

    public bool IsAvailable(ChannelAdapterKind kind) => _adapters.ContainsKey(kind);

    public IChannelAdapter? Find(ChannelAdapterKind kind) => _adapters.GetValueOrDefault(kind);
}

/// <summary>
/// The adapter of a channel sold by hand (docs/conception/phase-5-canaux.md §2): no network at all. Its bookings are
/// entered in the back office (manual entry), and its quota, stop-sell and price changes go to the "to replicate on
/// the extranet" list; nothing is ever sent or received.
/// </summary>
public sealed class ManualChannelAdapter : IChannelAdapter
{
    public ChannelAdapterKind Kind => ChannelAdapterKind.Manual;

    public Task<ChannelInboundResult> HandleInboundAsync(ChannelInboundMessage message, IChannelInboundCore core, CancellationToken cancellationToken) =>
        Task.FromResult(ChannelResponses.Problem(ChannelsErrors.InboundUnsupported, null));

    public Task<Result> PushAvailabilityAsync(ChannelPushTarget target, ChannelAvailabilityUpdate update, CancellationToken cancellationToken) =>
        Task.FromResult<Result>(ChannelsErrors.AdapterNotConfigured);

    public Task<Result> PushRatesAsync(ChannelPushTarget target, ChannelRatesUpdate update, CancellationToken cancellationToken) =>
        Task.FromResult<Result>(ChannelsErrors.AdapterNotConfigured);

    public Result<MappedReservation> MapBooking(ChannelReservation reservation, ChannelMappingSet mappings) =>
        ChannelBookingMapper.Map(reservation, mappings);
}

/// <summary>Helpers for adapters answering in JSON: status codes of the refusals and problem bodies.</summary>
public static class ChannelResponses
{
    public const string JsonContentType = "application/json; charset=utf-8";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The HTTP status of a refusal: 422 validation, 404 not found, 409 conflict, 403 forbidden, 502 failure.</summary>
    public static int StatusOf(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Kind switch
        {
            ErrorKind.NotFound => 404,
            ErrorKind.Conflict => 409,
            ErrorKind.Forbidden => 403,
            ErrorKind.Failure => 502,
            _ => 422,
        };
    }

    /// <summary>A JSON refusal <c>{"error": code, "message": developer text}</c> with the status of the error.</summary>
    public static ChannelInboundResult Problem(Error error, ChannelInboundRequest? request, ChannelInboundOutcome? outcome = null, int? statusCode = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { error = error.Code, message = error.Message }, JsonOptions);
        return new ChannelInboundResult(statusCode ?? StatusOf(error), JsonContentType, body, request, outcome, outcome is null ? error : null);
    }

    /// <summary>A JSON answer.</summary>
    public static ChannelInboundResult Json<T>(int statusCode, T value, ChannelInboundRequest request, ChannelInboundOutcome outcome) =>
        new(statusCode, JsonContentType, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), request, outcome);

    /// <summary>UTF-8 text of a body (adapters reading text formats).</summary>
    public static string Text(ReadOnlyMemory<byte> body) => Encoding.UTF8.GetString(body.Span);
}
