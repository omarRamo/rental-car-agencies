namespace CarRental.Web.Security;

/// <summary>
/// Extra sources a response needs beyond the base policy of <see cref="SecurityHeadersMiddleware"/>, requested by the
/// component that needs them during the rendering (e.g. the Turnstile widget: its script and its frame from
/// <c>https://challenges.cloudflare.com</c>). Only that response gets them.
/// </summary>
public sealed class ContentSecurityPolicyAdditions
{
    private readonly SortedSet<string> _scriptSources = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _frameSources = new(StringComparer.Ordinal);

    public bool IsEmpty => _scriptSources.Count == 0 && _frameSources.Count == 0;

    /// <summary>The additions of the current response, created on first use.</summary>
    public static ContentSecurityPolicyAdditions For(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var additions = context.Features.Get<ContentSecurityPolicyAdditions>();
        if (additions is null)
        {
            additions = new ContentSecurityPolicyAdditions();
            context.Features.Set(additions);
        }

        return additions;
    }

    public ContentSecurityPolicyAdditions AllowScript(string source)
    {
        _scriptSources.Add(Check(source));
        return this;
    }

    public ContentSecurityPolicyAdditions AllowFrame(string source)
    {
        _frameSources.Add(Check(source));
        return this;
    }

    /// <summary>The base policy (which declares <c>script-src 'self'</c> and no <c>frame-src</c>) extended with the additions.</summary>
    public string ApplyTo(string policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (_scriptSources.Count > 0)
        {
            policy = policy.Replace("script-src 'self'", "script-src 'self' " + string.Join(' ', _scriptSources), StringComparison.Ordinal);
        }

        return _frameSources.Count > 0 ? $"{policy}; frame-src 'self' {string.Join(' ', _frameSources)}" : policy;
    }

    /// <summary>Only https origins: no keyword, no wildcard, no separator that could inject a directive.</summary>
    private static string Check(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && !source.Contains(';', StringComparison.Ordinal)
        && !source.Contains('*', StringComparison.Ordinal)
        && !source.Contains(' ', StringComparison.Ordinal)
            ? uri.GetLeftPart(UriPartial.Authority)
            : throw new ArgumentException($"'{source}' is not an https origin.", nameof(source));
}
