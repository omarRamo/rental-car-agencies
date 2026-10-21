namespace CarRental.Web.Components.Public;

/// <summary>
/// Marks a public page without any POST form as cacheable by browsers and Cloudflare for
/// <see cref="MaxAgeSeconds"/> (ADR-0036): no antiforgery token is generated for it (no cookie, no <c>no-store</c>) and
/// its anonymous 200 responses get <c>Cache-Control: public, max-age=…</c>. The culture is part of the path, so each
/// language is cached under its own address. Never put it on a page with a POST form: the form would have no token.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CacheablePublicPageAttribute(int maxAgeSeconds = CacheablePublicPageAttribute.DefaultMaxAgeSeconds) : Attribute
{
    /// <summary>Five minutes: home prices and default dates stay fresh enough, Cloudflare absorbs the bursts.</summary>
    public const int DefaultMaxAgeSeconds = 300;

    public int MaxAgeSeconds { get; } = maxAgeSeconds;
}
