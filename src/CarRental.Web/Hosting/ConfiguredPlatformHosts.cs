using CarRental.Application.Referential.Contracts;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Hosting;

/// <summary>
/// The platform's own host names, from <see cref="HostFilteringSettings"/> (<c>HostFiltering:AllowedHosts</c> and
/// <c>HostFiltering:CustomDomains</c>): exact names and <c>*.suffix</c> patterns. The support cannot attach one of them
/// to an agency's site (ADR-0069). With <c>*</c> (development), only <c>localhost</c> and the loopback addresses are the
/// platform's.
/// </summary>
internal sealed class ConfiguredPlatformHosts : IPlatformHosts
{
    private static readonly string[] Loopback = ["localhost", "127.0.0.1", "::1"];

    private readonly string[] _exactHosts;
    private readonly string[] _suffixes;

    public ConfiguredPlatformHosts(IOptions<HostFilteringSettings> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var patterns = $"{options.Value.AllowedHosts};{options.Value.CustomDomains}"
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .Where(p => p != "*")
            .ToArray();
        _exactHosts = [.. patterns.Where(p => !p.StartsWith("*.", StringComparison.Ordinal)).Concat(Loopback).Distinct(StringComparer.Ordinal)];
        _suffixes = [.. patterns.Where(p => p.StartsWith("*.", StringComparison.Ordinal)).Select(p => p[1..])];
    }

    public bool IsPlatformHost(string hostName)
    {
        ArgumentNullException.ThrowIfNull(hostName);
        var normalized = hostName.Trim().ToLowerInvariant();
        return _exactHosts.Contains(normalized, StringComparer.Ordinal)
               || _suffixes.Any(suffix => normalized.EndsWith(suffix, StringComparison.Ordinal) && normalized.Length > suffix.Length);
    }
}
