using System.Globalization;
using System.Text;

namespace CarRental.Domain.Customers;

/// <summary>Trimming and validation shared by the Customers entities.</summary>
internal static class TextRules
{
    /// <summary>Trims and collapses inner whitespace; <see langword="null"/> for a blank value.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var space = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                space = true;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// A personal name: letters of any script (with their marks), spaces, hyphens, apostrophes and dots, at least one
    /// letter. Digits and symbols are rejected (they are typing mistakes or injection attempts).
    /// </summary>
    public static bool IsValidName(string? name, int maxLength)
    {
        if (string.IsNullOrEmpty(name) || name.Length > maxLength)
        {
            return false;
        }

        var hasLetter = false;
        foreach (var character in name)
        {
            if (char.IsLetter(character))
            {
                hasLetter = true;
                continue;
            }

            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                continue;
            }

            if (character is not (' ' or '-' or '\'' or '’' or '.'))
            {
                return false;
            }
        }

        return hasLetter;
    }
}
