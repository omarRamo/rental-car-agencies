using System.Text;
using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>
/// Phone numbers in E.164 (<c>+21698123456</c>), the key used to find a person again. A number without international
/// prefix is national to the default country (Tunisia: 8 digits, no trunk prefix); any other number needs <c>+</c> or
/// <c>00</c> and its country calling code. Spaces, dots, dashes, slashes, parentheses and the European "(0)" are ignored.
/// </summary>
/// <remarks>Only the structure is checked (no numbering plan beyond Tunisia's): the agent confirms by calling.</remarks>
public static class PhoneNumbers
{
    public const string TunisiaCallingCode = "216";

    /// <summary>'+' and at most 15 digits (E.164).</summary>
    public const int MaxLength = 16;

    private const int MinDigits = 8;
    private const int MaxDigits = 15;
    private const int TunisianSubscriberDigits = 8;

    public static Result<string> Normalize(string? value, string defaultCallingCode = TunisiaCallingCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultCallingCode);
        if (string.IsNullOrWhiteSpace(value))
        {
            return CustomersErrors.PhoneInvalid;
        }

        var text = value.Trim().Replace("(0)", string.Empty, StringComparison.Ordinal);
        string? digits;
        if (text.StartsWith('+'))
        {
            digits = DigitsOf(text.AsSpan(1));
        }
        else if (text.StartsWith("00", StringComparison.Ordinal))
        {
            digits = DigitsOf(text.AsSpan(2));
        }
        else
        {
            digits = National(DigitsOf(text), defaultCallingCode);
        }

        if (digits is null || !IsValidInternational(digits))
        {
            return CustomersErrors.PhoneInvalid;
        }

        return "+" + digits;
    }

    private static string? National(string? digits, string defaultCallingCode)
    {
        if (digits is null)
        {
            return null;
        }

        if (string.Equals(defaultCallingCode, TunisiaCallingCode, StringComparison.Ordinal))
        {
            // Tunisian numbers have 8 digits and no trunk prefix; "216" + 8 digits is a number typed without '+'.
            return digits.Length switch
            {
                TunisianSubscriberDigits => TunisiaCallingCode + digits,
                TunisianSubscriberDigits + 3 when digits.StartsWith(TunisiaCallingCode, StringComparison.Ordinal) => digits,
                _ => null,
            };
        }

        // Other countries: drop the national trunk prefix (0).
        return digits.StartsWith('0') ? defaultCallingCode + digits[1..] : defaultCallingCode + digits;
    }

    private static bool IsValidInternational(string digits)
    {
        if (digits.Length is < MinDigits or > MaxDigits || digits[0] == '0')
        {
            return false;
        }

        if (digits.StartsWith(TunisiaCallingCode, StringComparison.Ordinal))
        {
            // +216 then 8 digits; subscriber numbers never start with 0 or 1.
            return digits.Length == TunisiaCallingCode.Length + TunisianSubscriberDigits && digits[3] is >= '2' and <= '9';
        }

        return true;
    }

    /// <summary>Digits of <paramref name="text"/>, or null when it contains anything but digits and separators.</summary>
    private static string? DigitsOf(ReadOnlySpan<char> text)
    {
        var digits = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is >= '0' and <= '9')
            {
                digits.Append(character);
            }
            else if (character is not (' ' or ' ' or '.' or '-' or '/' or '(' or ')'))
            {
                return null;
            }
        }

        return digits.Length == 0 ? null : digits.ToString();
    }
}
