using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;

namespace CarRental.Application.Common.Email;

internal sealed class EmailQueue(IOutbox outbox, IAppDbContext db) : IEmailQueue
{
    public async Task EnqueueAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.To);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Subject);

        outbox.Enqueue(new EmailRequested(message));
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Sends queued e-mails. A failure is retried by the outbox processor with back-off.</summary>
internal sealed class EmailRequestedHandler(IEmailSender sender) : IOutboxHandler<EmailRequested>
{
    public Task HandleAsync(EmailRequested message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return sender.SendAsync(message.Message, cancellationToken);
    }
}
