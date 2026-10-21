using System.Globalization;
using System.Text;
using CarRental.Application.Channels.Adapters;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Domain.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Channels.Logging;

/// <summary>
/// Writes <see cref="ChannelMessageLog"/> rows in a DI scope of its own (its own <c>DbContext</c>), so that a row is kept
/// whatever happens to the business transaction (a refused or failed message is logged too). Never throws: a log that
/// cannot be written is reported to the application log and the channel still gets its answer.
/// </summary>
public sealed partial class ChannelMessageLogger
{
    internal const string ActorName = "system:channels-log";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChannelMessageLogger> _logger;

    internal ChannelMessageLogger(IServiceScopeFactory scopeFactory, ILogger<ChannelMessageLogger> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RecordAsync(ChannelMessageRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetService<ScopedUserContext>()?.Initialize(UserContextSnapshot.System(ActorName, record.CorrelationId));
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<ChannelMessageLog>().Add(ChannelMessageLog.Record(record));
            await db.SaveChangesAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // The log of a channel message never breaks the answer to the channel.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogFailed(exception, record.ChannelCode ?? "?", record.Operation.ToString());
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The message log row of channel {ChannelCode} ({Operation}) could not be written.")]
    private partial void LogFailed(Exception exception, string channelCode, string operation);
}

/// <summary>
/// Excerpts of channel messages for the log, built from the canonical form only: codes, instants, amounts and outcomes,
/// never a name, a phone number, an e-mail address or the free remarks of a booking.
/// </summary>
public static class ChannelExcerpts
{
    public static ChannelMessageOperation OperationOf(ChannelInboundRequest? request) => request?.Operation switch
    {
        ChannelInboundOperation.Availability => ChannelMessageOperation.Availability,
        ChannelInboundOperation.Create => ChannelMessageOperation.Create,
        ChannelInboundOperation.Modify => ChannelMessageOperation.Modify,
        ChannelInboundOperation.Cancel => ChannelMessageOperation.Cancel,
        ChannelInboundOperation.Read => ChannelMessageOperation.Read,
        _ => ChannelMessageOperation.Unknown,
    };

    /// <summary>What a request carried, without personal data.</summary>
    public static string Of(ChannelInboundRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request switch
        {
            ChannelAvailabilityRequest a => Invariant($"availability {a.PickupLocationCode}→{a.ReturnLocationCode ?? a.PickupLocationCode} {Instant(a.PickupAt)}/{Instant(a.ReturnAt)}{(a.CategoryCode is null ? string.Empty : " cat=" + a.CategoryCode)}"),
            ChannelCreateRequest c => "create " + Of(c.Reservation),
            ChannelModifyRequest m => "modify " + Of(m.Reservation),
            ChannelCancelRequest x => Invariant($"cancel ref={x.Reference}{(x.Commission is { } commission ? " commission=" + Amount(commission) : string.Empty)}"),
            ChannelReadRequest r => Invariant($"read ref={r.Reference}"),
            _ => request.Operation.ToString(),
        };
    }

    /// <summary>A reservation without its driver and remarks.</summary>
    public static string Of(ChannelReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        var text = new StringBuilder();
        text.Append(Invariant($"ref={reservation.ChannelReference} cat={reservation.CategoryCode} {reservation.PickupLocationCode}→{reservation.ReturnLocationCode} {Instant(reservation.PickupAt)}/{Instant(reservation.ReturnAt)} gross={Amount(reservation.Gross)}"));
        if (reservation.Commission is { } commission)
        {
            text.Append(" commission=").Append(Amount(commission));
        }

        if (reservation.Net is { } net)
        {
            text.Append(" net=").Append(Amount(net));
        }

        if (reservation.PaymentMode is { } mode)
        {
            text.Append(" mode=").Append(mode);
        }

        if (reservation.Extras is { Count: > 0 } extras)
        {
            text.Append(" extras=").AppendJoin(',', extras.Select(e => Invariant($"{e.Kind}:{e.Code}x{e.Quantity}")));
        }

        return text.ToString();
    }

    /// <summary>What the core answered: the offers, the booking and its status, or the refusal code.</summary>
    public static string? Of(ChannelInboundOutcome? outcome) => outcome?.Response switch
    {
        ChannelAvailabilityAnswer a => Invariant($"{a.Offers.Count} offer(s): {string.Join(',', a.Offers.Select(o => $"{o.CategoryCode}={o.Remaining}"))}"),
        ChannelReservationAnswer r => Invariant($"{r.Reservation.Status} {r.Reservation.BookingNumber}{(r.Replayed ? " (replayed)" : string.Empty)}"),
        _ => outcome?.Error?.Code,
    };

    private static string Amount(ChannelAmount amount) => Invariant($"{amount.Amount:0.###} {amount.Currency}");

    private static string Instant(DateTimeOffset instant) => instant.ToString("yyyy-MM-ddTHH:mmzzz", CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
