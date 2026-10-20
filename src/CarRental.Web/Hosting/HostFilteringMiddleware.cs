using Microsoft.Extensions.Options;

namespace CarRental.Web.Hosting;

/// <summary>
/// Rejects requests whose Host header is not an accepted host name (400). Health probes and the App Service warm-up
/// ping are never filtered. The built-in host filtering stays open (<c>AllowedHosts=*</c>) so that this one decides.
/// </summary>
internal sealed class HostFilteringMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _allowAll;
    private readonly string[] _exactHosts;
    private readonly string[] _suffixes;

    public HostFilteringMiddleware(RequestDelegate next, IOptions<HostFilteringSettings> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _next = next;
        var patterns = $"{options.Value.AllowedHosts};{options.Value.CustomDomains}"
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .ToArray();
        _allowAll = patterns.Contains("*");
        _exactHosts = patterns.Where(p => !p.StartsWith("*.", StringComparison.Ordinal)).ToArray();
        _suffixes = patterns.Where(p => p.StartsWith("*.", StringComparison.Ordinal)).Select(p => p[1..]).ToArray();
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_allowAll || IsExempt(context.Request.Path) || IsAllowed(context.Request.Host.Host))
        {
            return _next(context);
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return Task.CompletedTask;
    }

    internal static bool IsExempt(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/robots933456.txt", StringComparison.OrdinalIgnoreCase);

    private bool IsAllowed(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        var normalized = host.ToLowerInvariant();
        return _exactHosts.Contains(normalized, StringComparer.Ordinal)
               || _suffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.Ordinal) && normalized.Length > suffix.Length);
    }
}
