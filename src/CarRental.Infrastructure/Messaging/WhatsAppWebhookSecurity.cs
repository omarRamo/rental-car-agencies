using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Messaging;

/// <summary>
/// Checks of the WhatsApp webhook (Meta): the verification handshake (<c>hub.verify_token</c> equal to the configured
/// token) and the <c>X-Hub-Signature-256</c> header of every delivery (<c>sha256=</c> and the HMAC-SHA256 of the raw body
/// with the app secret, in lower-case hexadecimal). Both comparisons run in constant time.
/// </summary>
public sealed class WhatsAppWebhookSecurity(IOptions<WhatsAppOptions> options)
{
    /// <summary>Header carrying the signature of a delivery.</summary>
    public const string SignatureHeader = "X-Hub-Signature-256";

    private const string SignaturePrefix = "sha256=";
    private const int SignatureHexLength = 64;

    /// <summary>Whether the webhook is open (WhatsApp configured, fake or Graph API).</summary>
    public bool IsEnabled => options.Value.IsEnabled && !string.IsNullOrEmpty(options.Value.AppSecret);

    /// <summary>The signature header value of <paramref name="body"/> for <paramref name="appSecret"/> (as Meta computes it).</summary>
    public static string ComputeSignature(ReadOnlySpan<byte> body, string appSecret)
    {
        ArgumentException.ThrowIfNullOrEmpty(appSecret);
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        return SignaturePrefix + Convert.ToHexStringLower(hash);
    }

    /// <summary>True when <paramref name="token"/> is the configured verify token.</summary>
    public bool IsVerifyTokenValid(string? token)
    {
        var expected = options.Value.VerifyToken;
        if (!IsEnabled || string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(token))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    /// <summary>True when <paramref name="signatureHeader"/> is the signature of <paramref name="body"/> with the app secret.</summary>
    public bool IsSignatureValid(ReadOnlySpan<byte> body, string? signatureHeader)
    {
        var secret = options.Value.AppSecret;
        if (!IsEnabled || string.IsNullOrEmpty(secret) || signatureHeader is null)
        {
            return false;
        }

        var header = signatureHeader.Trim();
        if (header.Length != SignaturePrefix.Length + SignatureHexLength
            || !header.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] received;
        try
        {
            received = Convert.FromHexString(header.AsSpan(SignaturePrefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(received, expected);
    }
}
