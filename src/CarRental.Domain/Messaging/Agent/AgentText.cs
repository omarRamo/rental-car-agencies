using System.Globalization;
using System.Text;

namespace CarRental.Domain.Messaging.Agent;

/// <summary>
/// Text normalization shared by the guardrails of the WhatsApp agent: digits of every script as ASCII digits (Arabic-Indic
/// <c>٠١٢٣٤٥٦٧٨٩</c> and Eastern Arabic-Indic <c>۰…۹</c>), the Arabic decimal and thousands separators, the special
/// spaces as plain spaces, and — for matching — lower case without accents nor Arabic diacritics.
/// </summary>
public static class AgentText
{
    /// <summary>
    /// The text with ASCII digits and separators: Arabic-Indic and Eastern Arabic-Indic digits become 0-9, the Arabic
    /// decimal separator (U+066B) a dot, the Arabic thousands separator (U+066C) and the special spaces (no-break, narrow
    /// no-break, thin, figure) a plain space. Same length as the input.
    /// </summary>
    public static string NormalizeDigits(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character switch
            {
                >= '٠' and <= '٩' => (char)('0' + (character - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (character - '۰')),
                '٫' => '.',
                '٬' or ' ' or ' ' or ' ' or ' ' or ' ' or ' ' => ' ',
                _ => character,
            });
        }

        return builder.ToString();
    }

    /// <summary>
    /// The text for matching: digits normalized (<see cref="NormalizeDigits"/>), lower case (invariant), accents of the
    /// Latin letters and Arabic diacritics (harakat, tatweel) removed, typographic apostrophes as <c>'</c>, every run of
    /// white space as one space.
    /// </summary>
    public static string ForMatching(string? text)
    {
        var digits = NormalizeDigits(text);
        if (digits.Length == 0)
        {
            return string.Empty;
        }

        var decomposed = digits.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark || character == 'ـ')
            {
                // Accents of the Latin letters, Arabic harakat and the tatweel carry no meaning for matching.
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(character is '’' or '‘' or 'ʼ' or '`' ? '\'' : character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Whether <paramref name="character"/> is a letter of the Arabic script (Arabic, supplement and presentation forms).</summary>
    public static bool IsArabicLetter(char character) =>
        character is (>= 'ؠ' and <= 'ي') or (>= 'ٮ' and <= 'ۓ') or (>= 'ݐ' and <= 'ݿ')
            or (>= 'ﭐ' and <= '﷿') or (>= 'ﹰ' and <= '﻿');
}
