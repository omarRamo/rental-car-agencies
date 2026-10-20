using System.Text;

namespace CarRental.Domain.Customers;

/// <summary>
/// Identity document numbers. <see cref="Clean"/> gives the number as displayed (upper case, single spaces);
/// <see cref="KeyOf"/> gives the search and deduplication key (ASCII letters and digits only), so that
/// "12 ab-34567" and "12AB34567" are the same document.
/// </summary>
public static class DocumentNumbers
{
    public const int NumberMaxLength = 32;
    public const int KeyMinLength = 4;
    public const int KeyMaxLength = 20;

    /// <summary>Tunisian national identity card (CIN): exactly 8 digits.</summary>
    public const int TunisianNationalIdLength = 8;

    /// <summary>Upper-case, trimmed, inner whitespace collapsed; empty for a blank value.</summary>
    public static string Clean(string? value) => TextRules.Clean(value)?.ToUpperInvariant() ?? string.Empty;

    /// <summary>The ASCII letters and digits of <paramref name="value"/>, upper case.</summary>
    public static string KeyOf(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var key = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                key.Append(char.ToUpperInvariant(character));
            }
        }

        return key.ToString();
    }

    /// <summary>True when <paramref name="cleaned"/> only holds ASCII letters, digits and separators (space / - .).</summary>
    internal static bool HasOnlyAllowedCharacters(string cleaned) =>
        cleaned.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '/' or '-' or '.');
}
