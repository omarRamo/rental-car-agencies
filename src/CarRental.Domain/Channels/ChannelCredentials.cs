using System.Security.Cryptography;
using System.Text;
using CarRental.Domain.Common;

namespace CarRental.Domain.Channels;

/// <summary>
/// The API key a channel sends with every inbound message (header <c>X-Channel-Key</c>). Only its SHA-256 hash is
/// stored; the key is shown once, when it is generated. Keys carry 256 random bits, so a fast hash is enough (a slow
/// password hash protects low-entropy secrets), and the comparison runs in constant time.
/// </summary>
public static class ChannelApiKey
{
    /// <summary>Prefix of every key (recognizable in a secret scanner or a configuration file).</summary>
    public const string Prefix = "crk_";

    /// <summary>Random bytes of a key.</summary>
    public const int SecretBytes = 32;

    /// <summary>Length of the stored hash: <c>sha256:</c> and 64 hexadecimal digits.</summary>
    public const int HashLength = 71;

    /// <summary>Characters of the key kept to recognize it (<see cref="Prefix"/> and the first four characters of the secret).</summary>
    public const int HintLength = 8;

    private const string HashScheme = "sha256:";

    /// <summary>A new key, its stored hash and the hint shown to recognize it.</summary>
    public static GeneratedApiKey Generate()
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        var key = Prefix + secret;
        return new GeneratedApiKey(key, HashOf(key), key[..HintLength]);
    }

    /// <summary>The stored form of a key: <c>sha256:</c> and the upper-case hexadecimal SHA-256 of its UTF-8 bytes.</summary>
    public static string HashOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return HashScheme + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>
    /// Whether <paramref name="presented"/> is the key of <paramref name="storedHash"/>, compared in constant time; a
    /// missing key or hash never matches (a hash is still computed, so that timing does not tell them apart).
    /// </summary>
    public static bool Matches(string? presented, string? storedHash)
    {
        var candidate = Encoding.ASCII.GetBytes(HashOf(presented ?? string.Empty));
        var expected = Encoding.ASCII.GetBytes(storedHash ?? new string('0', HashLength));
        var equal = CryptographicOperations.FixedTimeEquals(candidate, expected);
        return equal && !string.IsNullOrEmpty(presented) && storedHash is not null;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>A key just generated: <see cref="Key"/> is shown once and never stored.</summary>
public sealed record GeneratedApiKey(string Key, string Hash, string Hint);

/// <summary>
/// Thumbprints of client certificates: upper-case hexadecimal, SHA-1 (40 digits, the thumbprint shown by Windows and
/// Azure) or SHA-256 (64 digits). Spaces and colons of a copied value are ignored.
/// </summary>
public static class CertificateThumbprints
{
    public const int Sha1Length = 40;

    public const int Sha256Length = 64;

    public static Result<string> Normalize(string? thumbprint)
    {
        var normalized = new string([.. (thumbprint ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != ':')]).ToUpperInvariant();
        return normalized.Length is Sha1Length or Sha256Length && normalized.All(char.IsAsciiHexDigitUpper)
            ? normalized
            : ChannelsErrors.CertificateThumbprintInvalid;
    }

    /// <summary>
    /// Whether the certificate presented (its SHA-1 and SHA-256 thumbprints, upper-case hexadecimal) is the expected one,
    /// compared in constant time on the algorithm of <paramref name="expected"/>.
    /// </summary>
    public static bool Matches(string? expected, string? presentedSha1, string? presentedSha256)
    {
        if (expected is null)
        {
            return false;
        }

        var presented = expected.Length == Sha1Length ? presentedSha1 : presentedSha256;
        if (presented is null || presented.Length != expected.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(presented.ToUpperInvariant()),
            Encoding.ASCII.GetBytes(expected));
    }
}
