using CarRental.Domain.Common;

namespace CarRental.Domain.Messaging;

/// <summary>
/// WhatsApp numbers in E.164 (<c>+21698123456</c>), the key of a conversation and of the link to a person (the Customers
/// module stores phones in the same format). Meta identifies a WhatsApp user by its <c>wa_id</c>, the same digits
/// without the <c>+</c>.
/// </summary>
public static class WhatsAppNumbers
{
    public const int MinDigits = 8;
    public const int MaxDigits = 15;

    /// <summary>'+' and at most 15 digits.</summary>
    public const int MaxLength = MaxDigits + 1;

    /// <summary>The E.164 form of a Meta <c>wa_id</c> (<c>21698123456</c>) or of an E.164 number (<c>+21698123456</c>).</summary>
    public static Result<string> Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return MessagingErrors.NumberInvalid;
        }

        var digits = value.Trim();
        if (digits.StartsWith('+'))
        {
            digits = digits[1..];
        }

        if (digits.Length is < MinDigits or > MaxDigits || digits[0] == '0' || !digits.All(char.IsAsciiDigit))
        {
            return MessagingErrors.NumberInvalid;
        }

        return "+" + digits;
    }

    /// <summary>The Meta <c>wa_id</c> (digits only) of an E.164 number, as expected by the <c>to</c> field of the Graph API.</summary>
    public static string ToWaId(string e164Number)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(e164Number);
        return e164Number.TrimStart('+');
    }
}
