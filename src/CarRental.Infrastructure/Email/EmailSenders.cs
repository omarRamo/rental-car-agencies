using System.Globalization;
using System.Text;
using Azure;
using Azure.Communication.Email;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Files;
using Microsoft.Extensions.Options;
using AcsEmailAttachment = Azure.Communication.Email.EmailAttachment;
using AcsEmailMessage = Azure.Communication.Email.EmailMessage;
using EmailAttachment = CarRental.Application.Common.Email.EmailAttachment;
using EmailMessage = CarRental.Application.Common.Email.EmailMessage;

namespace CarRental.Infrastructure.Email;

/// <summary>Azure Communication Services e-mail with the managed identity (<c>new EmailClient(endpoint, credential)</c>).</summary>
internal sealed class AcsEmailSender(EmailClient client, IOptions<EmailOptions> options, IFileStorage storage) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var content = new EmailContent(message.Subject) { Html = message.HtmlBody, PlainText = message.TextBody };
        var recipients = new EmailRecipients([new EmailAddress(message.To, message.ToName)]);
        var email = new AcsEmailMessage(options.Value.SenderAddress!, recipients, content);
        foreach (var attachment in message.Attachments)
        {
            var bytes = await EmailAttachments.LoadAsync(storage, attachment, cancellationToken);
            email.Attachments.Add(new AcsEmailAttachment(attachment.FileName, attachment.ContentType, BinaryData.FromBytes(bytes)));
        }

        // Accepted by ACS when this returns; delivery is asynchronous on the ACS side.
        await client.SendAsync(WaitUntil.Started, email, cancellationToken);
    }
}

/// <summary>
/// Development and test transport: writes each message as an RFC 5322 <c>.eml</c> file (open it with any mail client),
/// with its attachments read from the private storage at send time.
/// </summary>
public sealed class PickupDirectoryEmailSender(string directory, TimeProvider timeProvider, IFileStorage? storage = null) : IEmailSender
{
    public const string FromAddress = "no-reply@carrental.local";

    public string Directory { get; } = directory;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var attachments = new List<(EmailAttachment Attachment, byte[] Content)>(message.Attachments.Count);
        foreach (var attachment in message.Attachments)
        {
            if (storage is null)
            {
                throw new InvalidOperationException("The e-mail has attachments but no file storage is configured.");
            }

            attachments.Add((attachment, await EmailAttachments.LoadAsync(storage, attachment, cancellationToken)));
        }

        System.IO.Directory.CreateDirectory(Directory);
        var now = timeProvider.GetUtcNow();
        var fileName = string.Create(CultureInfo.InvariantCulture, $"{now:yyyyMMdd-HHmmssfff}-{Guid.CreateVersion7():N}.eml");
        await File.WriteAllTextAsync(Path.Combine(Directory, fileName), Render(message, now, attachments), Encoding.UTF8, cancellationToken);
    }

    internal static string Render(EmailMessage message, DateTimeOffset date, IReadOnlyList<(EmailAttachment Attachment, byte[] Content)>? attachments = null)
    {
        var alternative = $"=_carrental_{Guid.NewGuid():N}";
        var builder = new StringBuilder();
        builder.Append("From: ").Append(FromAddress).Append("\r\n");
        builder.Append("To: ").Append(message.ToName is null ? message.To : $"{EncodeWord(message.ToName)} <{message.To}>").Append("\r\n");
        builder.Append("Subject: ").Append(EncodeWord(message.Subject)).Append("\r\n");
        builder.Append("Date: ").Append(date.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        builder.Append("Message-ID: <").Append(Guid.CreateVersion7().ToString("N")).Append("@carrental.local>\r\n");
        builder.Append("MIME-Version: 1.0\r\n");
        if (attachments is not { Count: > 0 })
        {
            builder.Append("Content-Type: multipart/alternative; boundary=\"").Append(alternative).Append("\"\r\n\r\n");
            AppendAlternative(builder, alternative, message);
            return builder.ToString();
        }

        var mixed = $"=_carrental_mixed_{Guid.NewGuid():N}";
        builder.Append("Content-Type: multipart/mixed; boundary=\"").Append(mixed).Append("\"\r\n\r\n");
        builder.Append("--").Append(mixed).Append("\r\n");
        builder.Append("Content-Type: multipart/alternative; boundary=\"").Append(alternative).Append("\"\r\n\r\n");
        AppendAlternative(builder, alternative, message);
        foreach (var (attachment, content) in attachments)
        {
            var name = FileNameParameter(attachment.FileName);
            builder.Append("--").Append(mixed).Append("\r\n");
            builder.Append("Content-Type: ").Append(attachment.ContentType).Append("; name=").Append(name).Append("\r\n");
            builder.Append("Content-Disposition: attachment; filename=").Append(name).Append("\r\n");
            builder.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            AppendBase64(builder, content);
            builder.Append("\r\n");
        }

        builder.Append("--").Append(mixed).Append("--\r\n");
        return builder.ToString();
    }

    private static void AppendAlternative(StringBuilder builder, string boundary, EmailMessage message)
    {
        if (message.TextBody is not null)
        {
            AppendPart(builder, boundary, "text/plain", message.TextBody);
        }

        AppendPart(builder, boundary, "text/html", message.HtmlBody);
        builder.Append("--").Append(boundary).Append("--\r\n");
    }

    private static void AppendPart(StringBuilder builder, string boundary, string mediaType, string body)
    {
        builder.Append("--").Append(boundary).Append("\r\n");
        builder.Append("Content-Type: ").Append(mediaType).Append("; charset=utf-8\r\n");
        builder.Append("Content-Transfer-Encoding: base64\r\n\r\n");
        AppendBase64(builder, Encoding.UTF8.GetBytes(body));
        builder.Append("\r\n");
    }

    private static void AppendBase64(StringBuilder builder, byte[] content)
    {
        var encoded = Convert.ToBase64String(content);
        for (var i = 0; i < encoded.Length; i += 76)
        {
            builder.Append(encoded.AsSpan(i, Math.Min(76, encoded.Length - i))).Append("\r\n");
        }
    }

    /// <summary>A quoted ASCII file name as it is, any other name as an encoded word.</summary>
    private static string FileNameParameter(string fileName) =>
        fileName.All(c => c is >= ' ' and <= '~' and not '"' and not '\\') ? $"\"{fileName}\"" : $"\"{EncodeWord(fileName)}\"";

    private static string EncodeWord(string text) => $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}?=";
}

/// <summary>Loads the attachments of an e-mail from the private storage when the e-mail is sent.</summary>
internal static class EmailAttachments
{
    /// <summary>Largest attachment sent (the e-mail service accepts 10 MB per message).</summary>
    public const int MaxLength = 10 * 1024 * 1024;

    public static async Task<byte[]> LoadAsync(IFileStorage storage, EmailAttachment attachment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(attachment);

        // A missing file fails the sending: the outbox retries it, then shows it as failed (never sent without it).
        await using var download = await storage.OpenReadAsync(attachment.Container, attachment.Path, cancellationToken)
            ?? throw new InvalidOperationException($"The attachment {attachment.Container}/{attachment.Path} does not exist.");
        using var buffer = new MemoryStream();
        await download.Content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaxLength)
        {
            throw new InvalidOperationException($"The attachment {attachment.Container}/{attachment.Path} is larger than {MaxLength} bytes.");
        }

        return buffer.ToArray();
    }
}
