namespace CarRental.Web.Hosting;

/// <summary>Public site settings.</summary>
public sealed class PublicSiteOptions
{
    public const string SectionName = "PublicSite";

    /// <summary>
    /// Name of the platform: its own page at <c>/</c>, the account pages and the e-mails to the staff. The site of an agency
    /// and the e-mails to its customers carry the name of the agency (ADR-0067).
    /// </summary>
    public string AgencyName { get; set; } = "CarRental";

    /// <summary>
    /// WhatsApp number in international format without "+" (e.g. <c>21612345678</c>), the platform's: the floating button
    /// shows on the sites of the agencies whose WhatsApp module is on; hidden when empty.
    /// </summary>
    public string? WhatsAppNumber { get; set; }

    /// <summary>
    /// Absolute public URL of the platform used in e-mail links (e.g. <c>https://rental.app</c>), the address of an
    /// agency's site following it (<c>/agence-1/fr</c>); the request URL is used when empty.
    /// </summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Lets search engines index the site (robots.txt); defaults to true in Production only (recette stays hidden).</summary>
    public bool? AllowIndexing { get; set; }
}

/// <summary>
/// Accepted host names (protection against host header injection, e.g. in password reset links). Patterns separated
/// by ';': exact names or <c>*.suffix</c>; <c>*</c> accepts any host (development). Health probes are never filtered.
/// </summary>
public sealed class HostFilteringSettings
{
    public const string SectionName = "HostFiltering";

    public string AllowedHosts { get; set; } = "*";

    /// <summary>Custom domains of the deployment (e.g. <c>example.tn;www.example.tn</c>), added to <see cref="AllowedHosts"/>.</summary>
    public string? CustomDomains { get; set; }
}

/// <summary>Reverse proxy settings. Forwarded headers themselves are enabled by App Service (ASPNETCORE_FORWARDEDHEADERS_ENABLED).</summary>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    /// <summary>
    /// Reads the client IP from <c>CF-Connecting-IP</c> when present (Cloudflare in front of the app). Off by default: any
    /// client that reaches the origin directly can set this header and choose its address (rate limits, audit trail), so it
    /// is only turned on with the origin reserved to Cloudflare — the Bicep parameter <c>restrictToCloudflare</c> sets
    /// <c>ReverseProxy__TrustCloudflareHeader</c> (ADR-0062).
    /// </summary>
    public bool TrustCloudflareHeader { get; set; }
}

public sealed class LocalizationSettings
{
    public const string SectionName = "Localization";

    /// <summary>Arabic (right-to-left) is prepared but off until its resources are translated.</summary>
    public bool EnableArabic { get; set; }
}
