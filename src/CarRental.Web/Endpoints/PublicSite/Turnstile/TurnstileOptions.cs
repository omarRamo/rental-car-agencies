namespace CarRental.Web.Endpoints.PublicSite.Turnstile;

/// <summary>
/// Cloudflare Turnstile (anti-robot check of the public forms, ADR-0037). <see cref="SiteKey"/> is public (rendered in
/// the page); <see cref="SecretKey"/> is a secret (Key Vault <c>Turnstile--SecretKey</c> in Azure). Development and
/// tests fall back to the official test keys of Cloudflare, which always pass. Without keys elsewhere, verification fails
/// closed: protected submissions are refused, the rest of the site works.
/// </summary>
public sealed class TurnstileOptions
{
    public const string SectionName = "Turnstile";

    /// <summary>Name of the form field the widget fills with its token.</summary>
    public const string ResponseFieldName = "cf-turnstile-response";

    public string? SiteKey { get; set; }

    public string? SecretKey { get; set; }

    public Uri VerifyEndpoint { get; set; } = new("https://challenges.cloudflare.com/turnstile/v0/siteverify");

    /// <summary>
    /// Renders the widget without Cloudflare's script, as a hidden field holding the dummy test token (automated tests
    /// that must not depend on the internet). Never in production.
    /// </summary>
    public bool OfflineTestWidget { get; set; }

    /// <summary>
    /// Accepts the answers of Cloudflare's test keys (<c>metadata.result_with_testing_key</c>), which carry neither the
    /// action nor the real host name. Set from the environment only: outside production; never in production, where a
    /// test secret would accept a token anyone can write.
    /// </summary>
    public bool AcceptTestingKeys { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SiteKey) && !string.IsNullOrWhiteSpace(SecretKey);
}

/// <summary>
/// Official test keys of Cloudflare Turnstile (public values from the Cloudflare documentation, they protect nothing):
/// a site key that always passes, secrets that always pass or always fail, and the dummy token they accept.
/// </summary>
public static class TurnstileTestKeys
{
    public const string AlwaysPassesSiteKey = "1x00000000000000000000AA"; // gitleaks:allow (public Cloudflare test key)

    public const string AlwaysPassesSecretKey = "1x0000000000000000000000000000000AA"; // gitleaks:allow (public Cloudflare test key)

    public const string AlwaysFailsSecretKey = "2x0000000000000000000000000000000AA"; // gitleaks:allow (public Cloudflare test key)

    public const string DummyToken = "XXXX.DUMMY.TOKEN.XXXX";
}
