namespace CarRental.Application.Common.Email;

/// <summary>
/// E-mail transport (Azure Communication Services in Azure, .eml files in a local folder in development and tests).
/// Application code never calls it directly: it queues messages with <see cref="IEmailQueue"/> so that sending
/// happens through the outbox, after commit, with retries.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
