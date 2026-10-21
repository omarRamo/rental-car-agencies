namespace CarRental.Application.Common.Email;

/// <summary>An e-mail to send. Bodies are already rendered and localized.</summary>
/// <param name="To">Recipient address.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="HtmlBody">HTML body (encoded by the producer).</param>
/// <param name="TextBody">Plain-text alternative.</param>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string? TextBody = null)
{
    /// <summary>Optional display name of the recipient.</summary>
    public string? ToName { get; init; }

    /// <summary>
    /// Files attached by reference: the sender loads them from the private storage when it sends the message, so that
    /// the outbox payload stays small. A missing file fails the sending (retried by the outbox), never a silent omission.
    /// </summary>
    public IReadOnlyList<EmailAttachment> Attachments { get; init; } = [];
}

/// <summary>A file of the private storage attached to an e-mail: container and path, file name and content type shown to the recipient.</summary>
public sealed record EmailAttachment(string Container, string Path, string FileName, string ContentType);
