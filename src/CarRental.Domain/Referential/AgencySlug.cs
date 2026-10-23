using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using CarRental.Domain.Common;

namespace CarRental.Domain.Referential;

/// <summary>
/// The address of an agency's public site, first segment of its links (<c>https://rental.app/agence-1/fr</c>): lower-case
/// letters and digits separated by single hyphens, <see cref="MinLength"/> to <see cref="MaxLength"/> characters, unique
/// on the platform and never one of the <see cref="Reserved"/> words (paths of the platform itself).
/// </summary>
public static class AgencySlug
{
    public const int MinLength = 3;
    public const int MaxLength = 60;

    /// <summary>First path segments used by the platform (back office, accounts, APIs, files, framework, SEO).</summary>
    public static IReadOnlySet<string> Reserved { get; } = new[]
    {
        "admin", "account", "accounts", "api", "app", "assets", "auth", "blazor", "cdn", "connect", "content",
        "contrat", "culture", "dev", "docs", "documents", "error", "errors", "files", "fonts", "framework", "hangfire",
        "health", "help", "home", "images", "img", "jobs", "js", "lib", "login", "logout", "mail", "media", "not-found",
        "payments", "platform", "plateforme", "public", "robots", "signin", "signout", "sitemap", "static", "status",
        "support", "webhooks", "well-known", "www",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The slug as stored (trimmed, lower case) when it is valid and not reserved.</summary>
    public static Result<string> Normalize(string? value)
    {
        var slug = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsWellFormed(slug))
        {
            return ReferentialErrors.AgencySlugInvalid;
        }

        return Reserved.Contains(slug) ? ReferentialErrors.AgencySlugReserved : slug;
    }

    /// <summary>
    /// A slug proposed from the agency name: accents removed, lower case, every other character turned into a hyphen
    /// (<c>Agence Démo Tunis</c> → <c>agence-demo-tunis</c>), cut at <see cref="MaxLength"/>; <c>agence-…</c> when the
    /// name gives too few letters or a reserved word.
    /// </summary>
    public static string FromName(string? name)
    {
        var builder = new StringBuilder(MaxLength);
        var pendingHyphen = false;
        foreach (var character in (name ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(character);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(lower);
                pendingHyphen = false;
            }
            else
            {
                pendingHyphen = true;
            }

            if (builder.Length >= MaxLength)
            {
                break;
            }
        }

        var slug = builder.ToString(0, Math.Min(builder.Length, MaxLength)).Trim('-');
        if (slug.Length < MinLength || Reserved.Contains(slug))
        {
            slug = ("agence-" + slug).TrimEnd('-');
        }

        return slug.Length >= MinLength ? slug : "agence";
    }

    /// <summary>
    /// <paramref name="slug"/> with a numeric suffix (<c>-2</c>, <c>-3</c>…) that fits in <see cref="MaxLength"/>: the
    /// next proposal when the slug is already taken.
    /// </summary>
    public static string WithSuffix(string slug, int number)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 2);
        var suffix = "-" + number.ToString(CultureInfo.InvariantCulture);
        var stem = slug.Length + suffix.Length > MaxLength ? slug[..(MaxLength - suffix.Length)].TrimEnd('-') : slug;
        return stem + suffix;
    }

    private static bool IsWellFormed(string slug)
    {
        if (slug.Length is < MinLength or > MaxLength || slug[0] == '-' || slug[^1] == '-')
        {
            return false;
        }

        for (var i = 0; i < slug.Length; i++)
        {
            var character = slug[i];
            if (character == '-')
            {
                if (slug[i - 1] == '-')
                {
                    return false;
                }
            }
            else if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }
        }

        return true;
    }
}
