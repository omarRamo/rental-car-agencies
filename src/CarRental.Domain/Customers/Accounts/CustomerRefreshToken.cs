using CarRental.Domain.Common;

namespace CarRental.Domain.Customers.Accounts;

/// <summary>
/// A refresh token of a customer account (ADR-0070): a random secret held by the application (hash stored), valid once,
/// replaced by a new one of the same <see cref="Family"/> at each use (rotation). A token presented twice (stolen and
/// replayed) reveals itself: the service then revokes the whole family. Belongs to the agency of its account.
/// </summary>
public sealed class CustomerRefreshToken : Entity, IAgencyOwned
{
    public const int DeviceMaxLength = 100;
    public const int IpAddressMaxLength = 45;

    private CustomerRefreshToken()
    {
    }

    private CustomerRefreshToken(Guid agencyId, Guid accountId, byte[] tokenHash, Guid family, string? device, string? ipAddress, DateTimeOffset now, TimeSpan lifetime)
        : base(NewId())
    {
        AgencyId = agencyId;
        AccountId = accountId;
        TokenHash = tokenHash;
        Family = family;
        Device = device;
        IpAddress = ipAddress;
        CreatedAtUtc = now.ToUniversalTime();
        ExpiresAtUtc = CreatedAtUtc + lifetime;
    }

    public Guid AgencyId { get; private set; }

    public Guid AccountId { get; private set; }

    /// <summary>SHA-256 of the token, unique.</summary>
    public byte[] TokenHash { get; private set; } = null!;

    /// <summary>The chain of rotations started by one sign-in (one device): revoked together.</summary>
    public Guid Family { get; private set; }

    /// <summary>What the application says about the device (model, name), for the list of sessions of the customer.</summary>
    public string? Device { get; private set; }

    public string? IpAddress { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    /// <summary>When the token was exchanged for a new one (single use).</summary>
    public DateTimeOffset? UsedAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    /// <summary>A token of <paramref name="accountId"/> (an account of <paramref name="agencyId"/>), valid <paramref name="lifetime"/>.</summary>
    public static CustomerRefreshToken Issue(Guid agencyId, Guid accountId, byte[] tokenHash, Guid family, string? device, string? ipAddress, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        if (agencyId == Guid.Empty || accountId == Guid.Empty || family == Guid.Empty)
        {
            throw new ArgumentException("A refresh token belongs to an account of an agency and to a family.");
        }

        if (tokenHash.Length != AccountSecrets.HashLength)
        {
            throw new ArgumentException("A token hash is a SHA-256.", nameof(tokenHash));
        }

        var trimmedDevice = string.IsNullOrWhiteSpace(device) ? null : device.Trim();
        return new CustomerRefreshToken(
            agencyId,
            accountId,
            tokenHash,
            family,
            trimmedDevice?.Length > DeviceMaxLength ? trimmedDevice[..DeviceMaxLength] : trimmedDevice,
            ipAddress?.Length > IpAddressMaxLength ? ipAddress[..IpAddressMaxLength] : ipAddress,
            now,
            lifetime);
    }

    /// <summary>Not used, not revoked and not expired.</summary>
    public bool IsActiveAt(DateTimeOffset now) => UsedAtUtc is null && RevokedAtUtc is null && ExpiresAtUtc > now;

    public void MarkUsed(DateTimeOffset now) => UsedAtUtc ??= now.ToUniversalTime();

    public void Revoke(DateTimeOffset now) => RevokedAtUtc ??= now.ToUniversalTime();
}
