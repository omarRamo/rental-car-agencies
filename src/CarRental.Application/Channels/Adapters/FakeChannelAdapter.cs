using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;

namespace CarRental.Application.Channels.Adapters;

/// <summary>
/// TEST-ONLY channel adapter (docs/conception/phase-5-canaux.md §2 "Faux canal"), registered in Development and Testing
/// only. It speaks a small JSON format invented for the tests — it is NOT the Booking.com format, which is only known
/// once the integration specification is delivered — to exercise the whole inbound and outbound path:
/// <list type="bullet">
/// <item><c>POST availability</c> — <c>{"pickupLocation":"TUN","returnLocation":"TUN","pickupAt":"2027-07-01T10:00:00+01:00","returnAt":"…","category":"ECMR"}</c>
/// → 200 <c>{"offers":[{"category","acriss","remaining","days","total","deposit","currency"}]}</c>;</item>
/// <item><c>POST reservations</c> — a reservation (below) → 201 created, 200 when replayed;</item>
/// <item><c>PUT reservations/{reference}</c> — the new reservation → 200;</item>
/// <item><c>DELETE reservations/{reference}</c> — optional <c>{"commission":{"amount":20,"currency":"TND"}}</c> → 200;</item>
/// <item><c>GET reservations/{reference}</c> → 200.</item>
/// </list>
/// A reservation: <c>{"reference","category","pickupLocation","returnLocation","pickupAt","returnAt","driver":{"firstName",
/// "lastName","phone","email"},"gross":{"amount","currency"},"commission"?,"net"?,"paymentMode":"prepaid"|"onSite"?,
/// "extras":[{"kind":"extra"|"insurance"|"fee","code","quantity"}]?,"remarks"?}</c>; instants carry their offset. An
/// answer <c>{"status","reference","bookingNumber","category","pickupLocation","returnLocation","pickupAt","returnAt",
/// "gross","commission","net","currency","paymentMode","replayed"}</c>; a refusal <c>{"error":code,"message"}</c> (422,
/// 404, 409, 403 or 502). Pushes are recorded by <see cref="FakeChannelOutbox"/>, which can also fail them on purpose.
/// </summary>
public sealed class FakeChannelAdapter(FakeChannelOutbox outbox) : IChannelAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        NumberHandling = JsonNumberHandling.Strict,
    };

    public ChannelAdapterKind Kind => ChannelAdapterKind.Fake;

    public async Task<ChannelInboundResult> HandleInboundAsync(ChannelInboundMessage message, IChannelInboundCore core, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(core);
        var segments = message.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var method = message.Method.ToUpperInvariant();
        var parsed = (method, segments) switch
        {
            ("POST", ["availability"]) => ReadAvailability(message.Body),
            ("POST", ["reservations"]) => ReadReservation(message.Body, null).Map(r => (ChannelInboundRequest)new ChannelCreateRequest(r)),
            ("PUT", ["reservations", var reference]) => ReadReservation(message.Body, Uri.UnescapeDataString(reference)).Map(r => (ChannelInboundRequest)new ChannelModifyRequest(r)),
            ("DELETE", ["reservations", var reference]) => ReadCancel(message.Body, Uri.UnescapeDataString(reference)),
            ("GET", ["reservations", var reference]) => Result.Success<ChannelInboundRequest>(new ChannelReadRequest(Uri.UnescapeDataString(reference))),
            _ => Result.Failure<ChannelInboundRequest>(ChannelsErrors.InboundUnsupported),
        };
        if (parsed.IsFailure)
        {
            return ChannelResponses.Problem(parsed.Error, null, statusCode: parsed.Error == ChannelsErrors.InboundUnsupported ? 404 : 400);
        }

        var request = parsed.Value;
        var outcome = await core.HandleAsync(request, cancellationToken);
        if (outcome.Error is { } error)
        {
            return ChannelResponses.Problem(error, request, outcome);
        }

        return outcome.Response switch
        {
            ChannelAvailabilityAnswer availability => ChannelResponses.Json(200, new FakeAvailabilityAnswer([.. availability.Offers.Select(FakeOffer.Of)]), request, outcome),
            ChannelReservationAnswer reservation => ChannelResponses.Json(
                request.Operation == ChannelInboundOperation.Create && !reservation.Replayed ? 201 : 200,
                FakeReservationAnswer.Of(reservation),
                request,
                outcome),
            _ => ChannelResponses.Problem(ChannelsErrors.InboundUnsupported, request, outcome, 500),
        };
    }

    public Task<Result> PushAvailabilityAsync(ChannelPushTarget target, ChannelAvailabilityUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(update);
        return Task.FromResult(outbox.Receive(target.ChannelCode, ChannelSyncOperation.PushAvailability, JsonSerializer.Serialize(update, Json)));
    }

    public Task<Result> PushRatesAsync(ChannelPushTarget target, ChannelRatesUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(update);
        return Task.FromResult(outbox.Receive(target.ChannelCode, ChannelSyncOperation.PushRates, JsonSerializer.Serialize(update, Json)));
    }

    public Result<MappedReservation> MapBooking(ChannelReservation reservation, ChannelMappingSet mappings) => ChannelBookingMapper.Map(reservation, mappings);

    private static Result<ChannelInboundRequest> ReadAvailability(ReadOnlyMemory<byte> body)
    {
        var dto = Deserialize<FakeAvailabilityQuery>(body);
        if (dto is null || string.IsNullOrWhiteSpace(dto.PickupLocation) || Instant(dto.PickupAt) is not { } pickupAt || Instant(dto.ReturnAt) is not { } returnAt)
        {
            return ChannelsErrors.InboundMalformed;
        }

        return new ChannelAvailabilityRequest(dto.PickupLocation, dto.ReturnLocation, pickupAt, returnAt, dto.Category);
    }

    private static Result<ChannelReservation> ReadReservation(ReadOnlyMemory<byte> body, string? pathReference)
    {
        var dto = Deserialize<FakeReservation>(body);
        var reference = pathReference ?? dto?.Reference;
        if (dto is null
            || string.IsNullOrWhiteSpace(reference)
            || (pathReference is not null && dto.Reference is not null && !string.Equals(dto.Reference, pathReference, StringComparison.Ordinal))
            || string.IsNullOrWhiteSpace(dto.Category)
            || string.IsNullOrWhiteSpace(dto.PickupLocation)
            || Instant(dto.PickupAt) is not { } pickupAt
            || Instant(dto.ReturnAt) is not { } returnAt
            || dto.Driver is null
            || dto.Gross is null)
        {
            return ChannelsErrors.InboundMalformed;
        }

        var extras = new List<ChannelExtraRequest>();
        foreach (var extra in dto.Extras ?? [])
        {
            if (extra is null || string.IsNullOrWhiteSpace(extra.Code) || extra.Quantity is < 1 or > 99)
            {
                return ChannelsErrors.InboundMalformed;
            }

            extras.Add(new ChannelExtraRequest(extra.Kind ?? ChannelOptionKind.Extra, extra.Code, extra.Quantity));
        }

        return new ChannelReservation(
            reference,
            dto.Category,
            dto.PickupLocation,
            string.IsNullOrWhiteSpace(dto.ReturnLocation) ? dto.PickupLocation : dto.ReturnLocation,
            pickupAt,
            returnAt,
            new ChannelDriver(dto.Driver.FirstName ?? string.Empty, dto.Driver.LastName ?? string.Empty, dto.Driver.Phone, dto.Driver.Email),
            dto.Gross.ToAmount(),
            dto.Commission?.ToAmount(),
            dto.Net?.ToAmount(),
            dto.PaymentMode switch
            {
                FakePaymentMode.Prepaid => ChannelReservationPaymentMode.PrepaidToChannel,
                FakePaymentMode.OnSite => ChannelReservationPaymentMode.PayOnSite,
                _ => null,
            },
            extras,
            dto.Remarks);
    }

    private static Result<ChannelInboundRequest> ReadCancel(ReadOnlyMemory<byte> body, string reference)
    {
        if (body.IsEmpty)
        {
            return new ChannelCancelRequest(reference);
        }

        var dto = Deserialize<FakeCancellation>(body);
        return dto is null ? ChannelsErrors.InboundMalformed : new ChannelCancelRequest(reference, dto.Commission?.ToAmount());
    }

    private static T? Deserialize<T>(ReadOnlyMemory<byte> body)
        where T : class
    {
        if (body.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body.Span, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>An ISO 8601 instant with an explicit offset (<c>Z</c> or <c>±hh:mm</c>): a wall-clock time alone is ambiguous.</summary>
    private static DateTimeOffset? Instant(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 20)
        {
            return null;
        }

        var hasOffset = text.EndsWith('Z') || text[^6] is '+' or '-';
        return hasOffset && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant) ? instant : null;
    }

    private sealed record FakeAvailabilityQuery(string? PickupLocation, string? ReturnLocation, string? PickupAt, string? ReturnAt, string? Category);

    private sealed record FakeReservation(
        string? Reference,
        string? Category,
        string? PickupLocation,
        string? ReturnLocation,
        string? PickupAt,
        string? ReturnAt,
        FakeDriver? Driver,
        FakeAmount? Gross,
        FakeAmount? Commission,
        FakeAmount? Net,
        FakePaymentMode? PaymentMode,
        IReadOnlyList<FakeExtra?>? Extras,
        string? Remarks);

    private sealed record FakeDriver(string? FirstName, string? LastName, string? Phone, string? Email);

    private sealed record FakeAmount(decimal Amount, string? Currency)
    {
        public ChannelAmount ToAmount() => new(Amount, string.IsNullOrWhiteSpace(Currency) ? string.Empty : Currency);
    }

    private sealed record FakeExtra(ChannelOptionKind? Kind, string? Code, int Quantity);

    private sealed record FakeCancellation(FakeAmount? Commission);

    private enum FakePaymentMode
    {
        Prepaid = 1,
        OnSite = 2,
    }

    private sealed record FakeAvailabilityAnswer(IReadOnlyList<FakeOffer> Offers);

    private sealed record FakeOffer(string Category, string Acriss, int Remaining, int Days, decimal Total, decimal Deposit, string Currency)
    {
        public static FakeOffer Of(ChannelOffer offer) =>
            new(offer.CategoryCode, offer.AcrissCode, offer.Remaining, offer.Days, offer.TotalInclVat, offer.Deposit, offer.Currency);
    }

    private sealed record FakeReservationAnswer(
        string Status,
        string Reference,
        string BookingNumber,
        string? Category,
        string? PickupLocation,
        string? ReturnLocation,
        DateTimeOffset PickupAt,
        DateTimeOffset ReturnAt,
        decimal Gross,
        decimal Commission,
        decimal Net,
        decimal EngineTotal,
        string Currency,
        string PaymentMode,
        bool Replayed)
    {
        public static FakeReservationAnswer Of(ChannelReservationAnswer answer)
        {
            var view = answer.Reservation;
            return new FakeReservationAnswer(
                JsonNamingPolicy.CamelCase.ConvertName(view.Status.ToString()),
                view.ChannelReference,
                view.BookingNumber,
                view.CategoryCode,
                view.PickupLocationCode,
                view.ReturnLocationCode,
                view.PickupAt,
                view.ReturnAt,
                view.Gross,
                view.Commission,
                view.Net,
                view.EngineTotalInclVat,
                view.Currency,
                view.PaymentMode == ChannelReservationPaymentMode.PrepaidToChannel ? "prepaid" : "onSite",
                answer.Replayed);
        }
    }
}

/// <summary>
/// TEST-ONLY "remote side" of <see cref="FakeChannelAdapter"/>: records every update pushed to a fake channel and can
/// refuse the next pushes of a channel on purpose (retries, alerts).
/// </summary>
public sealed class FakeChannelOutbox
{
    private readonly ConcurrentQueue<FakeChannelPush> _received = new();
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every update accepted, oldest first.</summary>
    public IReadOnlyList<FakeChannelPush> Received => [.. _received];

    /// <summary>The updates accepted for one channel, oldest first.</summary>
    public IReadOnlyList<FakeChannelPush> ReceivedBy(string channelCode) =>
        [.. _received.Where(push => string.Equals(push.ChannelCode, channelCode, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The next <paramref name="count"/> pushes to <paramref name="channelCode"/> fail (as a channel outage would).</summary>
    public void FailNext(string channelCode, int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelCode);
        _failures[channelCode] = count;
    }

    internal Result Receive(string channelCode, ChannelSyncOperation operation, string payload)
    {
        while (_failures.TryGetValue(channelCode, out var left) && left > 0)
        {
            if (_failures.TryUpdate(channelCode, left - 1, left))
            {
                return Error.Failure(ChannelsErrors.PushFailed.Code, $"Simulated outage of the test channel {channelCode} (HTTP 503).");
            }
        }

        _received.Enqueue(new FakeChannelPush(channelCode, operation, payload));
        return Result.Success();
    }
}

/// <summary>An update received by a fake channel: the operation and its canonical JSON.</summary>
public sealed record FakeChannelPush(string ChannelCode, ChannelSyncOperation Operation, string Payload);
