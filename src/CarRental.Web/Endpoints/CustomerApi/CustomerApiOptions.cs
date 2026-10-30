using System.Text;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Endpoints.CustomerApi;

/// <summary>
/// The API of the mobile application and of the storefronts (ADR-0070, section <c>CustomerApi</c>): the signing key of
/// the access tokens (a secret, Key Vault <c>CustomerApi--Jwt--SigningKey</c>), their issuer, audience and lifetime, and
/// the web origins allowed to call the API from a browser (storefronts hosted elsewhere).
/// </summary>
public sealed class CustomerApiOptions
{
    public const string SectionName = "CustomerApi";

    /// <summary>Shortest key accepted (HMAC-SHA256 needs 256 bits).</summary>
    public const int MinimumKeyBytes = 32;

    public JwtOptions Jwt { get; set; } = new();

    /// <summary>Validity of an access token (15 minutes): a stolen token dies quickly, the refresh token renews it.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Origins (<c>https://www.agence.tn</c>) allowed to call the API from a browser; none: no browser other than the platform's own.</summary>
    public IList<string> AllowedOrigins { get; } = [];

    public sealed class JwtOptions
    {
        /// <summary>HMAC key of the access tokens, at least 32 bytes; required outside development and tests.</summary>
        public string? SigningKey { get; set; }

        public string Issuer { get; set; } = "carrental";

        public string Audience { get; set; } = "carrental-customers";

        /// <summary>The key as bytes (UTF-8).</summary>
        public byte[] KeyBytes => Encoding.UTF8.GetBytes(SigningKey ?? string.Empty);
    }
}

/// <summary>
/// Development and tests sign with a fixed key when none is configured (it protects nothing: these environments are
/// local); anywhere else a key of at least 32 bytes is required, and this key is refused.
/// </summary>
internal sealed class CustomerApiOptionsSetup(IHostEnvironment environment) : IPostConfigureOptions<CustomerApiOptions>, IValidateOptions<CustomerApiOptions>
{
    public const string DevelopmentSigningKey = "carrental-development-customer-api-signing-key-not-a-secret"; // gitleaks:allow (development/test-only key)

    public void PostConfigure(string? name, CustomerApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Jwt.SigningKey) && (environment.IsDevelopment() || environment.IsEnvironment(Hosting.WebEnvironments.Testing)))
        {
            options.Jwt.SigningKey = DevelopmentSigningKey;
        }
    }

    public ValidateOptionsResult Validate(string? name, CustomerApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var local = environment.IsDevelopment() || environment.IsEnvironment(Hosting.WebEnvironments.Testing);
        if (options.Jwt.KeyBytes.Length < CustomerApiOptions.MinimumKeyBytes)
        {
            return ValidateOptionsResult.Fail("CustomerApi:Jwt:SigningKey must hold at least 32 bytes (Key Vault secret CustomerApi--Jwt--SigningKey).");
        }

        if (!local && string.Equals(options.Jwt.SigningKey, DevelopmentSigningKey, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Fail("CustomerApi:Jwt:SigningKey is the development key: set a secret key outside development.");
        }

        if (string.IsNullOrWhiteSpace(options.Jwt.Issuer) || string.IsNullOrWhiteSpace(options.Jwt.Audience))
        {
            return ValidateOptionsResult.Fail("CustomerApi:Jwt:Issuer and CustomerApi:Jwt:Audience are required.");
        }

        if (options.AccessTokenLifetime < TimeSpan.FromMinutes(1) || options.AccessTokenLifetime > TimeSpan.FromHours(1))
        {
            return ValidateOptionsResult.Fail("CustomerApi:AccessTokenLifetime lies between one minute and one hour.");
        }

        if (options.AllowedOrigins.Any(o => !Uri.TryCreate(o, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(uri.PathAndQuery.TrimEnd('/'))))
        {
            return ValidateOptionsResult.Fail("CustomerApi:AllowedOrigins holds origins only (scheme and host, no path).");
        }

        return ValidateOptionsResult.Success;
    }
}
