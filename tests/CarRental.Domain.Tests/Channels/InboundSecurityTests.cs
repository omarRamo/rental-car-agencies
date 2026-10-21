using System.Net;
using CarRental.Domain.Channels;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CarRental.Domain.Tests.Channels;

/// <summary>Inbound security values: CIDR ranges, the fail-closed allowlist, API keys and certificate thumbprints.</summary>
public sealed class InboundSecurityTests
{
    [Theory]
    [InlineData("203.0.113.0/24", "203.0.113.0", true)]
    [InlineData("203.0.113.0/24", "203.0.113.255", true)]
    [InlineData("203.0.113.0/24", "203.0.114.1", false)]
    [InlineData("203.0.113.77/24", "203.0.113.1", true)]
    [InlineData("198.51.100.7", "198.51.100.7", true)]
    [InlineData("198.51.100.7", "198.51.100.8", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    [InlineData("10.0.0.0/8", "::ffff:10.1.2.3", true)]
    [InlineData("::ffff:10.0.0.0/104", "10.200.0.1", true)]
    [InlineData("2001:db8::/32", "2001:db8:1234::1", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    [InlineData("2001:db8::/32", "203.0.113.1", false)]
    [InlineData("::1", "::1", true)]
    [InlineData("203.0.113.0/25", "203.0.113.128", false)]
    public void A_range_contains_the_addresses_of_its_network(string range, string address, bool contains)
    {
        IpRange.Parse(range).Value.Contains(IPAddress.Parse(address)).ShouldBe(contains);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("10.1")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/ 8")]
    [InlineData("2001:db8::/129")]
    [InlineData("fe80::1%eth0")]
    [InlineData("300.1.1.1")]
    public void A_malformed_range_is_refused(string range)
    {
        IpRange.Parse(range).Error.ShouldBe(ChannelsErrors.IpRangeInvalid);
    }

    [Fact]
    public void A_range_is_normalized_to_its_network()
    {
        IpRange.Parse(" 203.0.113.77/24 ").Value.ToString().ShouldBe("203.0.113.0/24");
        IpRange.Parse("2001:db8:ffff::1/32").Value.ToString().ShouldBe("2001:db8::/32");
        IpRange.Parse("192.0.2.1").Value.PrefixLength.ShouldBe(32);
    }

    [Fact]
    public void An_empty_allowlist_allows_nothing()
    {
        var empty = IpAllowlist.Parse("  ").Value;

        empty.IsEmpty.ShouldBeTrue();
        empty.Allows(IPAddress.Loopback).ShouldBeFalse();
        empty.Allows(null).ShouldBeFalse();
        IpAllowlist.Parse("127.0.0.1;::1").Value.Allows(IPAddress.IPv6Loopback).ShouldBeTrue();
    }

    [Fact]
    public void An_allowlist_has_a_bounded_number_of_ranges()
    {
        var many = string.Join(';', Enumerable.Range(1, IpAllowlist.MaxRanges + 1).Select(i => $"10.0.{i}.0/24"));

        IpAllowlist.Parse(many).Error.ShouldBe(ChannelsErrors.IpRangesTooMany);
        IpAllowlist.Parse("10.0.0.0/8;bad").Error.ShouldBe(ChannelsErrors.IpRangeInvalid);
    }

    [Property(MaxTest = 300)]
    public Property Every_address_of_an_ipv4_network_is_contained_and_the_next_network_is_not(byte a, byte b, byte c, byte d, int prefixSeed)
    {
        var prefix = 8 + (Math.Abs(prefixSeed % 25));
        var range = IpRange.Parse($"{a}.{b}.{c}.{d}/{prefix}").Value;
        var network = BitConverter.ToUInt32([.. range.Network.GetAddressBytes().Reverse()], 0);
        var size = 1UL << (32 - prefix);
        var last = (uint)(network + size - 1);
        var outside = (ulong)network + size <= uint.MaxValue ? (uint?)(network + size) : null;

        return (range.Contains(ToAddress(network))
                && range.Contains(ToAddress(last))
                && range.Contains(IPAddress.Parse($"{a}.{b}.{c}.{d}"))
                && (outside is not { } next || !range.Contains(ToAddress(next))))
            .ToProperty();
    }

    [Fact]
    public void An_api_key_matches_its_hash_only()
    {
        var generated = ChannelApiKey.Generate();

        generated.Key.Length.ShouldBe(ChannelApiKey.Prefix.Length + 43);
        generated.Hash.Length.ShouldBe(ChannelApiKey.HashLength);
        generated.Hash.ShouldStartWith("sha256:");
        ChannelApiKey.Matches(generated.Key, generated.Hash).ShouldBeTrue();
        ChannelApiKey.Matches(generated.Key + "x", generated.Hash).ShouldBeFalse();
        ChannelApiKey.Matches(generated.Key.ToUpperInvariant(), generated.Hash).ShouldBeFalse();
        ChannelApiKey.Matches(null, generated.Hash).ShouldBeFalse();
        ChannelApiKey.Matches(string.Empty, ChannelApiKey.HashOf(string.Empty)).ShouldBeFalse("an empty key never authenticates");
        ChannelApiKey.Matches(generated.Key, null).ShouldBeFalse();
        ChannelApiKey.Generate().Key.ShouldNotBe(generated.Key);
    }

    [Fact]
    public void A_certificate_thumbprint_is_compared_on_its_own_algorithm()
    {
        var sha1 = new string('A', CertificateThumbprints.Sha1Length);
        var sha256 = new string('b', CertificateThumbprints.Sha256Length);

        CertificateThumbprints.Normalize(sha256).Value.ShouldBe(sha256.ToUpperInvariant());
        CertificateThumbprints.Normalize("ZZ" + sha1[2..]).Error.ShouldBe(ChannelsErrors.CertificateThumbprintInvalid);
        CertificateThumbprints.Matches(sha1, sha1, sha256).ShouldBeTrue();
        CertificateThumbprints.Matches(sha256.ToUpperInvariant(), sha1, sha256).ShouldBeTrue();
        CertificateThumbprints.Matches(sha1, new string('C', 40), sha256).ShouldBeFalse();
        CertificateThumbprints.Matches(sha1, null, null).ShouldBeFalse();
        CertificateThumbprints.Matches(null, sha1, sha256).ShouldBeFalse();
    }

    private static IPAddress ToAddress(uint value) => new([.. BitConverter.GetBytes(value).Reverse()]);
}
