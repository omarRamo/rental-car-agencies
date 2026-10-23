using System.Diagnostics;
using System.Net;
using CarRental.Application.Channels.Adapters;
using CarRental.Application.Channels.Core;
using CarRental.Application.Channels.Logging;
using CarRental.Application.Common.Security;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Channels.Inbound;

/// <summary>
/// An inbound HTTP message of a channel, as the endpoint <c>/api/channels/{code}/…</c> received it: address segment,
/// method, path and query, body (<see cref="BodyTooLarge"/> when cut at the limit), the API key header, the thumbprints
/// of the client certificate presented (if any), the client address and the correlation id.
/// </summary>
public sealed record ChannelInboundEnvelope(
    string ChannelCode,
    string Method,
    string Path,
    string? Query,
    string? ContentType,
    ReadOnlyMemory<byte> Body,
    bool BodyTooLarge,
    string? ApiKey,
    string? CertificateSha1,
    string? CertificateSha256,
    IPAddress? RemoteAddress,
    string? CorrelationId);

/// <summary>The HTTP answer to send to the channel.</summary>
public sealed record ChannelHttpAnswer(int StatusCode, string ContentType, ReadOnlyMemory<byte> Body);

/// <summary>
/// The inbound pipeline of the channels (docs/conception/phase-5-canaux.md §3 "Entrant"): the channel is found by its
/// address segment on the whole platform (codes are unique across agencies) and only answers while its agency is active
/// with its channels module on — otherwise it does not exist for the outside world (404, ADR-0067); then authentication
/// — the channel is connected, the caller's address is allowed (CIDR, fail closed), the API key matches its hash in
/// constant time and the client certificate its thumbprint when one is configured —, the adapter of the channel reads the
/// message, and the core answers it in a DI scope acting as the channel (<c>system:channel:{code}</c>) for the agency of
/// the channel only. Every message, accepted, refused or rejected, is written to the message log without personal data.
/// </summary>
public sealed partial class ChannelInboundService
{
    /// <summary>The actor that finds the channel of an address segment, across agencies.</summary>
    internal const string LookupActorName = "system:channels-inbound";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ChannelAdapterRegistry _adapters;
    private readonly ChannelMessageLogger _log;
    private readonly IAgencyDirectory _agencies;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChannelInboundService> _logger;

    internal ChannelInboundService(
        IServiceScopeFactory scopeFactory,
        ChannelAdapterRegistry adapters,
        ChannelMessageLogger log,
        IAgencyDirectory agencies,
        TimeProvider clock,
        ILogger<ChannelInboundService> logger)
    {
        _scopeFactory = scopeFactory;
        _adapters = adapters;
        _log = log;
        _agencies = agencies;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The system actor a channel's operations run as (timeline of the bookings, audit log).</summary>
    public static string ActorOf(string channelCode) => $"system:channel:{channelCode}";

    public async Task<ChannelHttpAnswer> HandleAsync(ChannelInboundEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = _clock.GetTimestamp();
        var occurredAt = _clock.GetUtcNow();

        var channel = await FindOpenChannelAsync(envelope, cancellationToken);
        var rejection = Authenticate(channel, envelope);
        if (rejection is not null)
        {
            await LogAsync(envelope, channel, null, null, rejection.Value.Error, rejection.Value.StatusCode, ChannelMessageStatus.Rejected, started, occurredAt);
            LogRejected(SafeCode(envelope.ChannelCode), rejection.Value.Error.Code, envelope.RemoteAddress?.ToString() ?? "?");
            return Problem(rejection.Value.Error, rejection.Value.StatusCode);
        }

        if (envelope.BodyTooLarge)
        {
            await LogAsync(envelope, channel, null, null, ChannelsErrors.InboundPayloadTooLarge, 413, ChannelMessageStatus.Failed, started, occurredAt);
            return Problem(ChannelsErrors.InboundPayloadTooLarge, 413);
        }

        var adapter = _adapters.Find(channel!.AdapterKind);
        if (adapter is null)
        {
            await LogAsync(envelope, channel, null, null, ChannelsErrors.AdapterNotConfigured, 501, ChannelMessageStatus.Failed, started, occurredAt);
            return Problem(ChannelsErrors.AdapterNotConfigured, 501);
        }

        // The message is processed for the agency of the channel only: the core sees and writes nothing of another agency.
        await using var scope = _scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(
            UserContextSnapshot.System(ActorOf(channel.Code), envelope.CorrelationId, channel.AgencyId) with { IpAddress = envelope.RemoteAddress?.ToString() });
        var contexts = scope.ServiceProvider.GetRequiredService<ChannelContextLoader>();
        var context = await contexts.ToContextAsync(channel, cancellationToken);
        var core = new BoundChannelCore(scope.ServiceProvider.GetRequiredService<ChannelReservationCore>(), context, adapter);
        ChannelInboundResult result;
        try
        {
            result = await adapter.HandleInboundAsync(new ChannelInboundMessage(channel.Code, envelope.Method, envelope.Path, envelope.Query, envelope.ContentType, envelope.Body), core, cancellationToken);
        }
#pragma warning disable CA1031 // Any technical failure becomes a 500 answer the channel can retry; it is logged.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            // A technical failure (database unavailable…): the channel gets an answer and sends the message again.
            LogFailed(exception, channel.Code);
            await LogAsync(envelope, channel, null, null, ChannelsErrors.InboundFailed, 500, ChannelMessageStatus.Failed, started, occurredAt, technical: true);
            return Problem(ChannelsErrors.InboundFailed, 500);
        }

        var status = result.Outcome switch
        {
            null => ChannelMessageStatus.Failed,
            { IsSuccess: true } => ChannelMessageStatus.Accepted,
            _ => ChannelMessageStatus.Refused,
        };
        await LogAsync(envelope, channel, result.Request, result.Outcome, result.Outcome?.Error ?? result.FormatError, result.StatusCode, status, started, occurredAt);
        return new ChannelHttpAnswer(result.StatusCode, result.ContentType, result.Body);
    }

    /// <summary>
    /// The channel of the address segment, found on the whole platform by a scope working across agencies (the code is
    /// unique on the platform); <see langword="null"/> when there is none, or when its agency is suspended or has its
    /// channels module off: the channel then does not exist for the outside world.
    /// </summary>
    private async Task<Channel?> FindOpenChannelAsync(ChannelInboundEnvelope envelope, CancellationToken cancellationToken)
    {
        Channel? channel;
        await using (var lookup = _scopeFactory.CreateAsyncScope())
        {
            lookup.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System(LookupActorName, envelope.CorrelationId));
            channel = await lookup.ServiceProvider.GetRequiredService<ChannelContextLoader>().FindByCodeAsync(envelope.ChannelCode, cancellationToken);
        }

        if (channel is not null && !await _agencies.HasModuleAsync(channel.AgencyId, AgencyModules.Channels, cancellationToken))
        {
            LogClosedAgency(channel.Code);
            return null;
        }

        return channel;
    }

    /// <summary>The first security check that fails, with the HTTP status to answer; <see langword="null"/> when the caller is the channel.</summary>
    private static (Error Error, int StatusCode)? Authenticate(Channel? channel, ChannelInboundEnvelope envelope)
    {
        if (channel is null)
        {
            // Still hash the presented key: an unknown channel answers as fast as a wrong key.
            _ = ChannelApiKey.Matches(envelope.ApiKey, null);
            return (ChannelsErrors.InboundUnknownChannel, 404);
        }

        if (channel.State != ChannelState.Connected)
        {
            return (ChannelsErrors.InboundNotConnected, 403);
        }

        if (!channel.IpAllowlist.Allows(envelope.RemoteAddress))
        {
            return (ChannelsErrors.InboundAddressNotAllowed, 403);
        }

        var keyMatches = channel.ApiKeyHash is null || channel.VerifyApiKey(envelope.ApiKey);
        var certificateMatches = channel.ClientCertificateThumbprint is null
            || CertificateThumbprints.Matches(channel.ClientCertificateThumbprint, envelope.CertificateSha1, envelope.CertificateSha256);
        return channel.HasInboundCredentials && keyMatches && certificateMatches ? null : (ChannelsErrors.InboundUnauthorized, 401);
    }

    private static ChannelHttpAnswer Problem(Error error, int statusCode)
    {
        var problem = ChannelResponses.Problem(error, null, statusCode: statusCode);
        return new ChannelHttpAnswer(problem.StatusCode, problem.ContentType, problem.Body);
    }

    /// <summary>The address segment as logged: the code of a channel, or a clipped placeholder for garbage.</summary>
    private static string SafeCode(string? code) =>
        ChannelCodes.ChannelCode(code) is { IsSuccess: true } valid ? valid.Value : "unknown";

    private async Task LogAsync(
        ChannelInboundEnvelope envelope,
        Channel? channel,
        ChannelInboundRequest? request,
        ChannelInboundOutcome? outcome,
        Error? error,
        int statusCode,
        ChannelMessageStatus status,
        long started,
        DateTimeOffset occurredAt,
        bool technical = false)
    {
        var excerpt = request is null
            ? Invariant($"{envelope.Method} {Clip(envelope.Path)} ({envelope.Body.Length} bytes)")
            : ChannelExcerpts.Of(request) + (ChannelExcerpts.Of(outcome) is { } answer ? " → " + answer : string.Empty);
        await _log.RecordAsync(new ChannelMessageRecord(
            channel?.Id,
            channel?.AgencyId,
            channel?.Code ?? SafeCode(envelope.ChannelCode),
            ChannelMessageDirection.Inbound,
            ChannelExcerpts.OperationOf(request),
            status,
            _clock.GetElapsedTime(started),
            occurredAt)
        {
            ChannelReference = request?.ChannelReference,
            HttpStatus = statusCode,
            Excerpt = technical ? excerpt + " — technical failure" : excerpt,
            ErrorCode = error?.Code,
            RemoteAddress = envelope.RemoteAddress?.ToString(),
            CorrelationId = envelope.CorrelationId ?? Activity.Current?.TraceId.ToString(),
            BookingId = outcome?.BookingId,
        });
    }

    /// <summary>The path of an unreadable message, cut short (it may carry anything).</summary>
    private static string Clip(string path) => path.Length > 80 ? path[..80] + "…" : path;

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbound message for channel {ChannelCode} rejected ({Reason}) from {RemoteAddress}.")]
    private partial void LogRejected(string channelCode, string reason, string remoteAddress);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbound message for channel {ChannelCode}: its agency is suspended or has the channels module off; answered as an unknown channel.")]
    private partial void LogClosedAgency(string channelCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Inbound message of channel {ChannelCode} failed.")]
    private partial void LogFailed(Exception exception, string channelCode);
}
