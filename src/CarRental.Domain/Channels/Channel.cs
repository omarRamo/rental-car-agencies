using CarRental.Domain.Common;

namespace CarRental.Domain.Channels;

/// <summary>
/// A sales channel of an agency (docs/conception/phase-5-canaux.md §1), aggregate of the Channels module: its code (the
/// address segment of the inbound endpoint), the sales channel its bookings carry, the adapter that speaks its format,
/// what the agency does with it (inactive, sold by hand on its extranet, connected), the inbound authentication (hashed
/// API key and/or client certificate thumbprint, allowed addresses), the commission rule, the default payment mode, and
/// its mappings.
/// </summary>
public sealed class Channel : AggregateRoot
{
    public const int NameMaxLength = 80;

    /// <summary>Highest commission rate (100 %).</summary>
    public const decimal MaxCommissionRate = 1m;

    private readonly List<ChannelMapping> _mappings = [];

    private Channel()
    {
    }

    private Channel(Guid agencyId, string code, string name, SalesChannel salesChannel, ChannelAdapterKind adapterKind, ChannelPaymentMode paymentMode, decimal commissionRate, ChannelStamp stamp)
        : base(NewId())
    {
        AgencyId = agencyId;
        Code = code;
        Name = name;
        SalesChannel = salesChannel;
        AdapterKind = adapterKind;
        State = ChannelState.Inactive;
        DefaultPaymentMode = paymentMode;
        CommissionRate = commissionRate;
        AllowedIpRanges = string.Empty;
        CreatedAtUtc = stamp.At;
        Touch(stamp);
    }

    /// <summary>The sales channels a <see cref="Channel"/> can carry: the comparators (Booking.com today).</summary>
    public static IReadOnlyList<SalesChannel> ExternalSalesChannels { get; } = [Common.SalesChannel.BookingCom];

    public Guid AgencyId { get; private set; }

    /// <summary>Lower-case code, unique on the platform: <c>/api/channels/{code}/…</c>.</summary>
    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    /// <summary>The channel its bookings, prices and availability are for (one channel per agency and sales channel).</summary>
    public SalesChannel SalesChannel { get; private set; }

    public ChannelAdapterKind AdapterKind { get; private set; }

    public ChannelState State { get; private set; }

    public ChannelPaymentMode DefaultPaymentMode { get; private set; }

    /// <summary>Commission of the channel as a fraction of the gross price (0.15 = 15 %), unless the channel reports the amount.</summary>
    public decimal CommissionRate { get; private set; }

    /// <summary>SHA-256 hash of the inbound API key (<see cref="ChannelApiKey"/>), never the key.</summary>
    public string? ApiKeyHash { get; private set; }

    /// <summary>The first characters of the key, to recognize it.</summary>
    public string? ApiKeyHint { get; private set; }

    public DateTimeOffset? ApiKeyCreatedAtUtc { get; private set; }

    /// <summary>Thumbprint (SHA-1 or SHA-256) of the client certificate the channel must present, if any.</summary>
    public string? ClientCertificateThumbprint { get; private set; }

    /// <summary>Allowed addresses in their stored form (<see cref="IpAllowlist.ToStorage"/>); none allowed when empty.</summary>
    public string AllowedIpRanges { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public string? UpdatedBy { get; private set; }

    public IReadOnlyList<ChannelMapping> Mappings => _mappings;

    /// <summary>The allowed addresses (an empty list allows nothing).</summary>
    public IpAllowlist IpAllowlist => IpAllowlist.Parse(AllowedIpRanges) is { IsSuccess: true } parsed ? parsed.Value : IpAllowlist.Empty;

    /// <summary>Whether an inbound message can authenticate at all (an API key or a certificate is configured).</summary>
    public bool HasInboundCredentials => ApiKeyHash is not null || ClientCertificateThumbprint is not null;

    public static Result<Channel> Create(
        Guid agencyId,
        string? code,
        string? name,
        SalesChannel salesChannel,
        ChannelAdapterKind adapterKind,
        ChannelPaymentMode paymentMode,
        decimal commissionRate,
        ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        if (agencyId == Guid.Empty)
        {
            return ChannelsErrors.AgencyRequired;
        }

        var normalizedCode = ChannelCodes.ChannelCode(code);
        if (normalizedCode.IsFailure)
        {
            return normalizedCode.Error;
        }

        var validName = ValidName(name);
        if (validName.IsFailure)
        {
            return validName.Error;
        }

        if (!ExternalSalesChannels.Contains(salesChannel))
        {
            return ChannelsErrors.SalesChannelInvalid;
        }

        if (!Enum.IsDefined(adapterKind))
        {
            return ChannelsErrors.AdapterInvalid;
        }

        var settings = ValidSettings(paymentMode, commissionRate);
        if (settings.IsFailure)
        {
            return settings.Error;
        }

        var channel = new Channel(agencyId, normalizedCode.Value, validName.Value, salesChannel, adapterKind, paymentMode, commissionRate, stamp);
        channel.Raise(new ChannelCreated(channel.Id, agencyId, channel.Code, salesChannel));
        return channel;
    }

    /// <summary>Changes the name, the default payment mode and the commission rate.</summary>
    public Result UpdateSettings(string? name, ChannelPaymentMode paymentMode, decimal commissionRate, ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var validName = ValidName(name);
        if (validName.IsFailure)
        {
            return validName.Error;
        }

        var settings = ValidSettings(paymentMode, commissionRate);
        if (settings.IsFailure)
        {
            return settings;
        }

        Name = validName.Value;
        DefaultPaymentMode = paymentMode;
        CommissionRate = commissionRate;
        Touch(stamp);
        Raise(new ChannelSettingsChanged(Id));
        return Result.Success();
    }

    /// <summary>Chooses the adapter; a connected channel cannot fall back to the manual adapter (make it manual first).</summary>
    public Result ChangeAdapter(ChannelAdapterKind adapterKind, ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        if (!Enum.IsDefined(adapterKind))
        {
            return ChannelsErrors.AdapterInvalid;
        }

        if (State == ChannelState.Connected && adapterKind == ChannelAdapterKind.Manual)
        {
            return ChannelsErrors.StateRequiresAdapter;
        }

        if (adapterKind == AdapterKind)
        {
            return Result.Success();
        }

        AdapterKind = adapterKind;
        Touch(stamp);
        Raise(new ChannelSettingsChanged(Id));
        return Result.Success();
    }

    /// <summary>Changes what the agency does with the channel; <see cref="ChannelState.Connected"/> needs a network adapter.</summary>
    public Result ChangeState(ChannelState state, ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        if (!Enum.IsDefined(state))
        {
            return ChannelsErrors.StateInvalid;
        }

        if (state == ChannelState.Connected && AdapterKind == ChannelAdapterKind.Manual)
        {
            return ChannelsErrors.StateRequiresAdapter;
        }

        if (state == State)
        {
            return Result.Success();
        }

        var previous = State;
        State = state;
        Touch(stamp);
        Raise(new ChannelStateChanged(Id, previous, state));
        return Result.Success();
    }

    /// <summary>Replaces the inbound API key; the returned key is shown once (only its hash is kept).</summary>
    public GeneratedApiKey RotateApiKey(ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var generated = ChannelApiKey.Generate();
        ApiKeyHash = generated.Hash;
        ApiKeyHint = generated.Hint;
        ApiKeyCreatedAtUtc = stamp.At;
        Touch(stamp);
        Raise(new ChannelCredentialsChanged(Id, ChannelCredentialsChange.ApiKeyRotated));
        return generated;
    }

    /// <summary>Removes the inbound API key: messages must then authenticate with the client certificate, if any.</summary>
    public void RevokeApiKey(ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        if (ApiKeyHash is null)
        {
            return;
        }

        ApiKeyHash = null;
        ApiKeyHint = null;
        ApiKeyCreatedAtUtc = null;
        Touch(stamp);
        Raise(new ChannelCredentialsChanged(Id, ChannelCredentialsChange.ApiKeyRevoked));
    }

    /// <summary>Sets (or removes, with an empty value) the thumbprint of the client certificate required from the channel.</summary>
    public Result SetClientCertificate(string? thumbprint, ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            var valid = CertificateThumbprints.Normalize(thumbprint);
            if (valid.IsFailure)
            {
                return valid.Error;
            }

            normalized = valid.Value;
        }

        if (string.Equals(normalized, ClientCertificateThumbprint, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        ClientCertificateThumbprint = normalized;
        Touch(stamp);
        Raise(new ChannelCredentialsChanged(Id, ChannelCredentialsChange.Certificate));
        return Result.Success();
    }

    /// <summary>Replaces the allowed addresses (CIDR ranges separated by <c>;</c>, <c>,</c>, spaces or new lines).</summary>
    public Result SetAllowedIpRanges(string? ranges, ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var allowlist = IpAllowlist.Parse(ranges);
        if (allowlist.IsFailure)
        {
            return allowlist.Error;
        }

        var stored = allowlist.Value.ToStorage();
        if (string.Equals(stored, AllowedIpRanges, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        AllowedIpRanges = stored;
        Touch(stamp);
        Raise(new ChannelCredentialsChanged(Id, ChannelCredentialsChange.IpRanges));
        return Result.Success();
    }

    /// <summary>Whether <paramref name="presentedKey"/> is the inbound API key (constant time).</summary>
    public bool VerifyApiKey(string? presentedKey) => ChannelApiKey.Matches(presentedKey, ApiKeyHash);

    /// <summary>
    /// Maps a category to the code of the channel, sold under its own ACRISS code or under <paramref name="acrissCode"/>;
    /// mapping it again replaces its codes.
    /// </summary>
    public Result<ChannelMapping> MapCategory(Guid categoryId, string? channelCode, string? acrissCode, ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        if (categoryId == Guid.Empty)
        {
            return ChannelsErrors.MappingTargetRequired;
        }

        string? acriss = null;
        if (!string.IsNullOrWhiteSpace(acrissCode))
        {
            var parsed = Common.AcrissCode.Create(acrissCode);
            if (parsed.IsFailure)
            {
                return ChannelsErrors.MappingAcrissInvalid;
            }

            acriss = parsed.Value.Value;
        }

        return Map(ChannelMappingKind.Category, categoryId, null, channelCode, acriss, stamp);
    }

    /// <summary>Maps a pick-up or return location to the code of the channel (e.g. <c>TUN</c>).</summary>
    public Result<ChannelMapping> MapLocation(Guid locationId, string? channelCode, ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        return locationId == Guid.Empty
            ? ChannelsErrors.MappingTargetRequired
            : Map(ChannelMappingKind.Location, locationId, null, channelCode, null, stamp);
    }

    /// <summary>Maps an extra, an insurance or a fee of the rate plan (by its code) to the OpenTravel code entered by the agency.</summary>
    public Result<ChannelMapping> MapOption(ChannelMappingKind kind, string? internalCode, string? channelCode, ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        if (kind is not (ChannelMappingKind.Extra or ChannelMappingKind.Insurance or ChannelMappingKind.Fee))
        {
            return ChannelsErrors.MappingKindInvalid;
        }

        var code = ChannelCodes.InternalCode(internalCode);
        return code.IsFailure ? code.Error : Map(kind, null, code.Value, channelCode, null, stamp);
    }

    public Result RemoveMapping(Guid mappingId, ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var mapping = _mappings.Find(m => m.Id == mappingId);
        if (mapping is null)
        {
            return ChannelsErrors.MappingNotFound;
        }

        _mappings.Remove(mapping);
        Touch(stamp);
        Raise(new ChannelMappingsChanged(Id, mapping.Kind));
        return Result.Success();
    }

    /// <summary>The mapping of a category or location, or <see langword="null"/>.</summary>
    public ChannelMapping? MappingOf(ChannelMappingKind kind, Guid internalId) =>
        _mappings.Find(m => m.Kind == kind && m.InternalId == internalId);

    /// <summary>The mapping of a channel code (case ignored), or <see langword="null"/>.</summary>
    public ChannelMapping? MappingOfCode(ChannelMappingKind kind, string? channelCode) =>
        _mappings.Find(m => m.Kind == kind && ChannelCodes.SameCode(m.ChannelCode, channelCode?.Trim()));

    private Result<ChannelMapping> Map(ChannelMappingKind kind, Guid? internalId, string? internalCode, string? channelCode, string? acrissCode, ChannelStamp stamp)
    {
        var code = ChannelCodes.MappingCode(channelCode);
        if (code.IsFailure)
        {
            return code.Error;
        }

        var existing = _mappings.Find(m => m.SameInternal(kind, internalId, internalCode));
        if (_mappings.Any(m => m.Kind == kind && m != existing && ChannelCodes.SameCode(m.ChannelCode, code.Value)))
        {
            return ChannelsErrors.MappingCodeTaken;
        }

        if (existing is not null)
        {
            existing.Change(code.Value, acrissCode, stamp.At);
        }
        else
        {
            existing = internalId is { } id
                ? ChannelMapping.ById(Id, kind, id, code.Value, acrissCode, stamp.At)
                : ChannelMapping.ByCode(Id, kind, internalCode!, code.Value, stamp.At);
            _mappings.Add(existing);
        }

        Touch(stamp);
        Raise(new ChannelMappingsChanged(Id, kind));
        return existing;
    }

    private void Touch(ChannelStamp stamp)
    {
        UpdatedAtUtc = stamp.At;
        UpdatedBy = stamp.By;
    }

    private static Result<string> ValidName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > NameMaxLength ? ChannelsErrors.NameInvalid : trimmed;
    }

    private static Result ValidSettings(ChannelPaymentMode paymentMode, decimal commissionRate)
    {
        if (!Enum.IsDefined(paymentMode))
        {
            return ChannelsErrors.PaymentModeInvalid;
        }

        return commissionRate is < 0m or > MaxCommissionRate || decimal.Round(commissionRate, 4) != commissionRate
            ? ChannelsErrors.CommissionRateInvalid
            : Result.Success();
    }
}
