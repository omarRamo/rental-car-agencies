using CarRental.Domain.Common;

namespace CarRental.Domain.Channels;

/// <summary>
/// A correspondence between the platform and a channel (docs/conception/phase-5-canaux.md §1): a category (its ACRISS
/// code, possibly sold under another one) and the code of the channel; a location and the code of the channel (TUN,
/// NBE, MIR, DJE…); an extra, an insurance or a fee and the OpenTravel Alliance code entered by the agency. Part of the
/// <see cref="Channel"/> aggregate, which keeps both sides unique and gives it its agency.
/// </summary>
public sealed class ChannelMapping : Entity, IAgencyOwned
{
    private ChannelMapping()
    {
    }

    private ChannelMapping(Guid channelId, Guid agencyId, ChannelMappingKind kind, Guid? internalId, string? internalCode, string channelCode, string? acrissCode, DateTimeOffset at)
        : base(NewId())
    {
        ChannelId = channelId;
        AgencyId = agencyId;
        Kind = kind;
        InternalId = internalId;
        InternalCode = internalCode;
        ChannelCode = channelCode;
        AcrissCode = acrissCode;
        CreatedAtUtc = at;
        UpdatedAtUtc = at;
    }

    public Guid ChannelId { get; private set; }

    /// <summary>The agency of the channel.</summary>
    public Guid AgencyId { get; private set; }

    public ChannelMappingKind Kind { get; private set; }

    /// <summary>The category or the location (by id; other modules are only known by id).</summary>
    public Guid? InternalId { get; private set; }

    /// <summary>The extra code of the rate plan catalogue (extras, insurances and fees).</summary>
    public string? InternalCode { get; private set; }

    /// <summary>The code of the channel.</summary>
    public string ChannelCode { get; private set; } = null!;

    /// <summary>The ACRISS code sold on the channel when it differs from the category's own code (categories only).</summary>
    public string? AcrissCode { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Whether the mapping is of a category or a location (by id) rather than of an extra code.</summary>
    public bool IsById => Kind is ChannelMappingKind.Category or ChannelMappingKind.Location;

    internal static ChannelMapping ById(Guid channelId, Guid agencyId, ChannelMappingKind kind, Guid internalId, string channelCode, string? acrissCode, DateTimeOffset at) =>
        new(channelId, agencyId, kind, internalId, null, channelCode, acrissCode, at);

    internal static ChannelMapping ByCode(Guid channelId, Guid agencyId, ChannelMappingKind kind, string internalCode, string channelCode, DateTimeOffset at) =>
        new(channelId, agencyId, kind, null, internalCode, channelCode, null, at);

    internal bool SameInternal(ChannelMappingKind kind, Guid? internalId, string? internalCode) =>
        Kind == kind && InternalId == internalId && string.Equals(InternalCode, internalCode, StringComparison.Ordinal);

    internal void Change(string channelCode, string? acrissCode, DateTimeOffset at)
    {
        ChannelCode = channelCode;
        AcrissCode = acrissCode;
        UpdatedAtUtc = at;
    }
}
