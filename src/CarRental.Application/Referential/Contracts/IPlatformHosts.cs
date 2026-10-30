namespace CarRental.Application.Referential.Contracts;

/// <summary>
/// The host names of the platform itself (its App Service address, its custom domains, <c>localhost</c>): never the own
/// host of an agency's site (ADR-0069). Known by the web host (its host filtering settings); the application refuses
/// to attach such a name to an agency.
/// </summary>
public interface IPlatformHosts
{
    /// <summary>True when <paramref name="hostName"/> (lower case, without port) is one of the platform's own host names.</summary>
    bool IsPlatformHost(string hostName);
}

/// <summary>No platform host known (tests, tools): every name may serve an agency.</summary>
public sealed class NoPlatformHosts : IPlatformHosts
{
    public bool IsPlatformHost(string hostName) => false;
}
