using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;

namespace CarRental.Application.Messaging.Webhooks;

/// <summary>
/// Outbox message holding the raw body of a webhook delivery whose signature was verified: processed asynchronously by
/// <see cref="WhatsAppWebhookHandler"/>. The CLR name is the outbox type: never rename it.
/// </summary>
/// <param name="Payload">The body as received (JSON of Meta, UTF-8).</param>
/// <param name="ReceivedAtUtc">When the platform received it.</param>
public sealed record WhatsAppWebhookReceived(string Payload, DateTimeOffset ReceivedAtUtc);

/// <summary>
/// Stores a verified webhook delivery for asynchronous processing (one outbox row, nothing else): the endpoint answers
/// Meta as soon as it is saved, and no business work happens in the request. Also used by the fake to simulate Meta.
/// </summary>
public sealed class WhatsAppWebhookInbox(IOutbox outbox, IAppDbContext db, TimeProvider clock)
{
    public async Task AcceptAsync(string payload, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        outbox.Enqueue(new WhatsAppWebhookReceived(payload, clock.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
    }
}
