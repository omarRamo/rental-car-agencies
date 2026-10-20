namespace CarRental.Application.Common.Email;

/// <summary>Queues an e-mail in the outbox; it is sent asynchronously by <see cref="IEmailSender"/> once committed.</summary>
public interface IEmailQueue
{
    /// <summary>Adds the message to the outbox and saves (joins the ambient transaction, if any).</summary>
    Task EnqueueAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Outbox message asking for an e-mail to be sent.</summary>
public sealed record EmailRequested(EmailMessage Message);
