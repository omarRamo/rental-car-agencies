using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace CarRental.Testing;

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 30-second steps, 6 digits), the algorithm of ASP.NET Core Identity's authenticator.</summary>
public static class Totp
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Compute(string base32Key, DateTimeOffset? at = null)
    {
        var key = Base32Decode(base32Key);
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);

#pragma warning disable CA5350 // RFC 6238 authenticators use HMAC-SHA1 (test helper reproducing Identity's algorithm).
        var hash = HMACSHA1.HashData(key, message);
#pragma warning restore CA5350

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    public static byte[] Base32Decode(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = input.Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        var output = new byte[text.Length * 5 / 8];
        int buffer = 0, bitsLeft = 0, index = 0;
        foreach (var character in text)
        {
            var value = Base32Alphabet.IndexOf(character, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException($"'{character}' is not a Base32 character.");
            }

            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output[index++] = (byte)(buffer >> (bitsLeft - 8));
                bitsLeft -= 8;
            }
        }

        return output;
    }
}
