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
}
