using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CarRental.Domain.Customers.Accounts;

/// <summary>
/// Secrets of the customer accounts (ADR-0070): refresh tokens carried by the mobile application and six-digit codes
/// e-mailed to the customer (e-mail verification, password reset). Only their SHA-256 hash is stored, so a copy of the
/// database opens nothing; comparisons run in constant time. Same shapes as the secrets of the public pages.
/// </summary>
public static class AccountSecrets
{
    /// <summary>256 random bits.</summary>
    public const int TokenBytes = 32;

    /// <summary>Length of a token in base64url without padding.</summary>
    public const int TokenLength = 43;

    public const int HashLength = 32;

    public const int CodeLength = 6;

    public const int SaltLength = 16;

    /// <summary>A new refresh token: 256 random bits in base64url (letters, digits, '-' and '_').</summary>
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>Whether <paramref name="token"/> has the shape of a token (checked before any database access).</summary>
    public static bool IsWellFormedToken(string? token) =>
        token is { Length: TokenLength } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The stored form of a token.</summary>
    public static byte[] HashToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>A new six-digit code (uniform over 000000–999999).</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>The digits of a typed code (spaces and dashes removed), or <see langword="null"/> when it is not six digits.</summary>
    public static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 20)
        {
            return null;
        }

        var digits = new string([.. code.Where(c => !char.IsWhiteSpace(c) && c != '-')]);
        return digits.Length == CodeLength && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltLength);

    /// <summary>The stored form of a code: SHA-256 of a random salt and the code.</summary>
    public static byte[] HashCode(string code, ReadOnlySpan<byte> salt)
    {
        ArgumentNullException.ThrowIfNull(code);
        var bytes = Encoding.UTF8.GetBytes(code);
        var input = new byte[salt.Length + bytes.Length];
        salt.CopyTo(input);
        bytes.CopyTo(input.AsSpan(salt.Length));
        return SHA256.HashData(input);
    }

    /// <summary>Constant-time comparison of two hashes.</summary>
    public static bool SameHash(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);

    /// <summary>A new security stamp (32 hexadecimal characters), changed whenever the credentials change.</summary>
    public static string NewStamp() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
