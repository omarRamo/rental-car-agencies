using System.Collections.Concurrent;
using CarRental.Application.Common.Security;
using CarRental.Application.Messaging.Sending;
using CarRental.Application.Messaging.Webhooks;
using CarRental.Domain.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Messaging;

/// <summary>A message the fake "sent": recipient, content, Meta id and when.</summary>
public sealed record FakeWhatsAppMessage(string To, MessageContent Content, string MetaMessageId, DateTimeOffset SentAtUtc);

/// <summary>
/// WhatsApp without Meta (development, tests, E2E; refused in production): records every message sent, answers with a
/// fake Meta id, and — like Meta — reports "sent" then "delivered" through the webhook pipeline
/// (<see cref="WhatsAppWebhookInbox"/>, so the real parser and processor run). It also simulates customers: an incoming
/// text, a button reply, an image, a status. A test can make the sends to one number fail (<see cref="FailSendsTo"/>).
/// </summary>
public sealed partial class FakeWhatsApp(
    IServiceScopeFactory scopeFactory,
    IOptions<WhatsAppOptions> options,
    TimeProvider clock,
    ILogger<FakeWhatsApp> logger) : IWhatsAppClient
{
    private const string ActorName = "system:fake-whatsapp";

    private readonly ConcurrentQueue<FakeWhatsAppMessage> _sent = new();
    private readonly ConcurrentDictionary<string, WhatsAppSendResult> _failures = new(StringComparer.Ordinal);

    public bool IsEnabled => true;

    /// <summary>Every message sent so far, in order.</summary>
    public IReadOnlyList<FakeWhatsAppMessage> Sent => [.. _sent];

    /// <summary>Messages sent to a <c>wa_id</c> (or E.164 number).</summary>
    public IReadOnlyList<FakeWhatsAppMessage> SentTo(string number) =>
        [.. _sent.Where(m => string.Equals(m.To, number.TrimStart('+'), StringComparison.Ordinal))];

    /// <summary>From now on, sends to <paramref name="number"/> end with <paramref name="result"/> (a refusal, a transient failure…).</summary>
    public void FailSendsTo(string number, WhatsAppSendResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        ArgumentNullException.ThrowIfNull(result);
        _failures[number.TrimStart('+')] = result;
    }

    public async Task<WhatsAppSendResult> SendAsync(WhatsAppSendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_failures.TryGetValue(request.To, out var failure))
        {
            LogFailed(failure.Outcome.ToString());
            return failure;
        }

        var metaId = $"wamid.FAKE{Guid.NewGuid():N}";
        _sent.Enqueue(new FakeWhatsAppMessage(request.To, request.Content, metaId, clock.GetUtcNow()));
        LogSent(request.Content.Kind.ToString());
        if (options.Value.FakeSimulatesStatuses)
        {
            var now = clock.GetUtcNow();
            await SimulateStatusAsync(metaId, request.To, "sent", now, cancellationToken: cancellationToken);
            await SimulateStatusAsync(metaId, request.To, "delivered", now, cancellationToken: cancellationToken);
        }

        return WhatsAppSendResult.Accepted(metaId);
    }

    /// <summary>A customer writes <paramref name="text"/>; returns the Meta id of the message.</summary>
    public async Task<string> SimulateIncomingTextAsync(string from, string? profileName, string text, CancellationToken cancellationToken = default)
    {
        var metaId = $"wamid.FAKEIN{Guid.NewGuid():N}";
        await AcceptAsync(
            MetaWebhookPayloads.TextMessage(BusinessAccountId, PhoneNumberId, from.TrimStart('+'), profileName, metaId, clock.GetUtcNow(), text),
            cancellationToken);
        return metaId;
    }

    /// <summary>A customer taps a reply button; returns the Meta id of the message.</summary>
    public async Task<string> SimulateButtonReplyAsync(string from, string? profileName, string buttonId, string title, CancellationToken cancellationToken = default)
    {
        var metaId = $"wamid.FAKEIN{Guid.NewGuid():N}";
        await AcceptAsync(
            MetaWebhookPayloads.ButtonReply(BusinessAccountId, PhoneNumberId, from.TrimStart('+'), profileName, metaId, clock.GetUtcNow(), buttonId, title),
            cancellationToken);
        return metaId;
    }

    /// <summary>Meta reports <paramref name="status"/> (<c>sent</c>, <c>delivered</c>, <c>read</c>, <c>failed</c>) for a sent message.</summary>
    public Task SimulateStatusAsync(
        string metaMessageId,
        string recipient,
        string status,
        DateTimeOffset? at = null,
        int? errorCode = null,
        string? errorTitle = null,
        CancellationToken cancellationToken = default) =>
        AcceptAsync(
            MetaWebhookPayloads.Status(BusinessAccountId, PhoneNumberId, metaMessageId, recipient.TrimStart('+'), status, at ?? clock.GetUtcNow(), errorCode: errorCode, errorTitle: errorTitle),
            cancellationToken);

    private string BusinessAccountId => options.Value.BusinessAccountId ?? WhatsAppDevelopmentKeys.BusinessAccountId;

    private string PhoneNumberId => options.Value.PhoneNumberId ?? WhatsAppDevelopmentKeys.PhoneNumberId;

    /// <summary>Hands a payload to the webhook pipeline, in a scope of its own (committed at once).</summary>
    private async Task AcceptAsync(string payload, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System(ActorName));
        await scope.ServiceProvider.GetRequiredService<WhatsAppWebhookInbox>().AcceptAsync(payload, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Fake WhatsApp: {Kind} message recorded (not sent to Meta).")]
    private partial void LogSent(string kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fake WhatsApp: simulated send outcome {Outcome}.")]
    private partial void LogFailed(string outcome);
}

/// <summary>WhatsApp not configured: nothing can be sent (the inbox and the notifications say so).</summary>
internal sealed class DisabledWhatsAppClient : IWhatsAppClient
{
    public static DisabledWhatsAppClient Instance { get; } = new();

    public bool IsEnabled => false;

    public Task<WhatsAppSendResult> SendAsync(WhatsAppSendRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(WhatsAppSendResult.Rejected(null, "WhatsApp is not configured."));
}
