using System.Globalization;
using System.Text;
using Azure;
using Azure.Communication.Email;
using CarRental.Application.Common.Email;
using Microsoft.Extensions.Options;
using AcsEmailMessage = Azure.Communication.Email.EmailMessage;
using EmailMessage = CarRental.Application.Common.Email.EmailMessage;

namespace CarRental.Infrastructure.Email;

/// <summary>Azure Communication Services e-mail with the managed identity (<c>new EmailClient(endpoint, credential)</c>).</summary>
internal sealed class AcsEmailSender(EmailClient client, IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var content = new EmailContent(message.Subject) { Html = message.HtmlBody, PlainText = message.TextBody };
        var recipients = new EmailRecipients([new EmailAddress(message.To, message.ToName)]);
        var email = new AcsEmailMessage(options.Value.SenderAddress!, recipients, content);

        // Accepted by ACS when this returns; delivery is asynchronous on the ACS side.
        await client.SendAsync(WaitUntil.Started, email, cancellationToken);
    }
}

/// <summary>Development and test transport: writes each message as an RFC 5322 <c>.eml</c> file (open it with any mail client).</summary>
public sealed class PickupDirectoryEmailSender(string directory, TimeProvider timeProvider) : IEmailSender
{
    public const string FromAddress = "no-reply@carrental.local";

    public string Directory { get; } = directory;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        System.IO.Directory.CreateDirectory(Directory);
        var now = timeProvider.GetUtcNow();
        var fileName = string.Create(CultureInfo.InvariantCulture, $"{now:yyyyMMdd-HHmmssfff}-{Guid.CreateVersion7():N}.eml");
        await File.WriteAllTextAsync(Path.Combine(Directory, fileName), Render(message, now), Encoding.UTF8, cancellationToken);
    }

    internal static string Render(EmailMessage message, DateTimeOffset date)
    {
        var boundary = $"=_carrental_{Guid.NewGuid():N}";
        var builder = new StringBuilder();
        builder.Append("From: ").Append(FromAddress).Append("\r\n");
        builder.Append("To: ").Append(message.ToName is null ? message.To : $"{EncodeWord(message.ToName)} <{message.To}>").Append("\r\n");
        builder.Append("Subject: ").Append(EncodeWord(message.Subject)).Append("\r\n");
        builder.Append("Date: ").Append(date.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        builder.Append("Message-ID: <").Append(Guid.CreateVersion7().ToString("N")).Append("@carrental.local>\r\n");
        builder.Append("MIME-Version: 1.0\r\n");
        builder.Append("Content-Type: multipart/alternative; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
        if (message.TextBody is not null)
        {
            AppendPart(builder, boundary, "text/plain", message.TextBody);
        }

        AppendPart(builder, boundary, "text/html", message.HtmlBody);
        builder.Append("--").Append(boundary).Append("--\r\n");
        return builder.ToString();
    }

    private static void AppendPart(StringBuilder builder, string boundary, string mediaType, string body)
    {
        builder.Append("--").Append(boundary).Append("\r\n");
        builder.Append("Content-Type: ").Append(mediaType).Append("; charset=utf-8\r\n");
        builder.Append("Content-Transfer-Encoding: base64\r\n\r\n");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        for (var i = 0; i < encoded.Length; i += 76)
        {
            builder.Append(encoded.AsSpan(i, Math.Min(76, encoded.Length - i))).Append("\r\n");
        }

        builder.Append("\r\n");
    }

    private static string EncodeWord(string text) => $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}?=";
}
