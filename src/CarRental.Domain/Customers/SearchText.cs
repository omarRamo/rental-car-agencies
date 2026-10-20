using System.Globalization;
using System.Text;

namespace CarRental.Domain.Customers;

/// <summary>
/// Text normalized for searching: lower case, accents removed, any run of non-alphanumeric characters (spaces,
/// hyphens, apostrophes…) collapsed to one space. "Hédi Ben-Salah" and "hedi ben salah" have the same search text.
/// </summary>
public static class SearchText
{
    /// <summary>The search text of <paramref name="value"/>, cut to <paramref name="maxLength"/> characters (decomposition can lengthen some scripts).</summary>
    public static string Normalize(string? value, int maxLength)
    {
        var normalized = Normalize(value);
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength].TrimEnd();
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Surrogates and unassigned code points are treated as separators: Normalize() may reject them, and no name
        // uses astral characters.
        var safe = string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var character = source[i];
                span[i] = char.IsSurrogate(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.OtherNotAssigned
                    ? ' '
                    : character;
            }
        });

        var decomposed = safe.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var separator = false;
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (separator && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                separator = false;
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                separator = true;
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
