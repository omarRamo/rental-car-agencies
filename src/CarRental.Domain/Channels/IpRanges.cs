using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CarRental.Domain.Common;

namespace CarRental.Domain.Channels;

/// <summary>
/// A range of IP addresses in CIDR notation (<c>203.0.113.0/24</c>, <c>2001:db8::/32</c>; a single address is a /32 or
/// a /128). The network address is normalized (host bits cleared). An IPv4 address mapped to IPv6
/// (<c>::ffff:203.0.113.7</c>) is matched as the IPv4 address it carries.
/// </summary>
public sealed class IpRange : IEquatable<IpRange>
{
    private readonly byte[] _network;

    private IpRange(byte[] network, int prefixLength, AddressFamily family)
    {
        _network = network;
        PrefixLength = prefixLength;
        Family = family;
    }

    public int PrefixLength { get; }

    public AddressFamily Family { get; }

    /// <summary>The first address of the range.</summary>
    public IPAddress Network => new(_network);

    public static bool operator ==(IpRange? left, IpRange? right) => Equals(left, right);

    public static bool operator !=(IpRange? left, IpRange? right) => !Equals(left, right);

    public static Result<IpRange> Parse(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 64)
        {
            return ChannelsErrors.IpRangeInvalid;
        }

        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        var addressText = slash < 0 ? trimmed : trimmed[..slash];
        // Dotted quads only for IPv4 (IPAddress also reads shorthands such as "10.1"), no IPv6 zone.
        if (!IPAddress.TryParse(addressText, out var address)
            || addressText.Contains('%', StringComparison.Ordinal)
            || (address.AddressFamily == AddressFamily.InterNetwork && addressText.Count(c => c == '.') != 3))
        {
            return ChannelsErrors.IpRangeInvalid;
        }

        var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = bits;
        if (slash >= 0
            && (!int.TryParse(trimmed.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > bits))
        {
            return ChannelsErrors.IpRangeInvalid;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            // ::ffff:a.b.c.d/n is the IPv4 range a.b.c.d/(n − 96).
            if (prefix < 96)
            {
                return ChannelsErrors.IpRangeInvalid;
            }

            address = address.MapToIPv4();
            prefix -= 96;
        }

        var bytes = address.GetAddressBytes();
        Mask(bytes, prefix);
        return new IpRange(bytes, prefix, address.AddressFamily);
    }

    /// <summary>Whether <paramref name="address"/> belongs to the range.</summary>
    public bool Contains(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != Family)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        Mask(bytes, PrefixLength);
        return bytes.AsSpan().SequenceEqual(_network);
    }

    public bool Equals(IpRange? other) =>
        other is not null && PrefixLength == other.PrefixLength && Family == other.Family && _network.AsSpan().SequenceEqual(other._network);

    public override bool Equals(object? obj) => obj is IpRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(PrefixLength, Family, Convert.ToHexString(_network));

    /// <summary>The normalized CIDR text, e.g. <c>203.0.113.0/24</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Network}/{PrefixLength}");

    private static void Mask(byte[] bytes, int prefixLength)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsInByte = Math.Clamp(prefixLength - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bitsInByte));
        }
    }
}

/// <summary>
/// The addresses a channel may call the platform from: a list of <see cref="IpRange"/>. It fails closed — an empty
/// list allows nothing — so that a channel is never reachable from anywhere by accident.
/// </summary>
public sealed class IpAllowlist
{
    public const int MaxRanges = 50;

    /// <summary>Longest stored form.</summary>
    public const int StorageMaxLength = 2000;

    private static readonly char[] Separators = [';', ',', ' ', '\n', '\r', '\t'];

    private IpAllowlist(IReadOnlyList<IpRange> ranges) => Ranges = ranges;

    public static IpAllowlist Empty { get; } = new([]);

    public IReadOnlyList<IpRange> Ranges { get; }

    public bool IsEmpty => Ranges.Count == 0;

    /// <summary>
    /// Parses ranges separated by <c>;</c>, <c>,</c>, spaces or new lines; duplicates are dropped and the order kept.
    /// </summary>
    public static Result<IpAllowlist> Parse(string? text)
    {
        var ranges = new List<IpRange>();
        foreach (var part in (text ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = IpRange.Parse(part);
            if (range.IsFailure)
            {
                return range.Error;
            }

            if (!ranges.Contains(range.Value))
            {
                ranges.Add(range.Value);
            }
        }

        if (ranges.Count > MaxRanges)
        {
            return ChannelsErrors.IpRangesTooMany;
        }

        var allowlist = new IpAllowlist(ranges);
        return allowlist.ToStorage().Length > StorageMaxLength ? ChannelsErrors.IpRangesTooMany : allowlist;
    }

    /// <summary>Whether <paramref name="address"/> is in one of the ranges (never when the list is empty).</summary>
    public bool Allows(IPAddress? address) => address is not null && Ranges.Any(range => range.Contains(address));

    /// <summary>Stored form: normalized ranges separated by <c>;</c>.</summary>
    public string ToStorage() => string.Join(';', Ranges.Select(range => range.ToString()));
}
