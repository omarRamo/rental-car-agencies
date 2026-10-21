using CarRental.Domain.Messaging;

namespace CarRental.Application.Messaging.Sending;

/// <summary>
/// Port to the WhatsApp Cloud API (Graph API <c>POST /{phone-number-id}/messages</c>): implemented by the Graph API
/// client, by <c>FakeWhatsApp</c> in development and tests, and by a disabled client when WhatsApp is not configured.
/// Application code never calls it directly: messages go through the outbox (<see cref="OutboundMessageSender"/>).
/// </summary>
public interface IWhatsAppClient
{
    /// <summary>Whether messages can be sent at all (false when WhatsApp is not configured).</summary>
    bool IsEnabled { get; }

    /// <summary>Sends one message; never throws for an answer of Meta or a network failure (see <see cref="WhatsAppSendOutcome"/>).</summary>
    Task<WhatsAppSendResult> SendAsync(WhatsAppSendRequest request, CancellationToken cancellationToken);
}

/// <summary>A message to send to <paramref name="To"/>, the customer's <c>wa_id</c> (digits of the E.164 number).</summary>
public sealed record WhatsAppSendRequest(string To, MessageContent Content);

/// <summary>What became of a send, and whether it is safe to try again.</summary>
public enum WhatsAppSendOutcome
{
    /// <summary>Meta accepted the message and gave its id; delivery statuses follow through the webhook.</summary>
    Accepted = 1,

    /// <summary>Meta refused the message (invalid, window closed, template unknown, blocked number…): never retried.</summary>
    Rejected = 2,

    /// <summary>
    /// The message certainly did not go out (connection refused, rate limited, service temporarily unavailable): safe to
    /// try again later.
    /// </summary>
    Transient = 3,

    /// <summary>The request may have reached Meta without an answer (timeout, broken response): not retried, to avoid a duplicate.</summary>
    Unknown = 4,
}

/// <summary>Outcome of a send, with the Meta message id when accepted and the Meta error otherwise.</summary>
public sealed record WhatsAppSendResult(WhatsAppSendOutcome Outcome, string? MetaMessageId = null, int? ErrorCode = null, string? ErrorTitle = null)
{
    public static WhatsAppSendResult Accepted(string metaMessageId) => new(WhatsAppSendOutcome.Accepted, metaMessageId);

    public static WhatsAppSendResult Rejected(int? errorCode, string? errorTitle) => new(WhatsAppSendOutcome.Rejected, null, errorCode, errorTitle);

    public static WhatsAppSendResult Transient(int? errorCode = null, string? errorTitle = null) => new(WhatsAppSendOutcome.Transient, null, errorCode, errorTitle);

    public static WhatsAppSendResult Unknown(int? errorCode = null, string? errorTitle = null) => new(WhatsAppSendOutcome.Unknown, null, errorCode, errorTitle);
}

/// <summary>
/// The WhatsApp service could not take a message for now (transient outcome): the outbox tries again later, with
/// growing delays.
/// </summary>
public sealed class WhatsAppUnavailableException : Exception
{
    public WhatsAppUnavailableException()
    {
    }

    public WhatsAppUnavailableException(string message)
        : base(message)
    {
    }

    public WhatsAppUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
