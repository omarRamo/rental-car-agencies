using CarRental.Application.Channels.Adapters;
using CarRental.Application.Channels.Outbound;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Channels.Administration;

/// <summary>
/// The channels screen (permission <c>channels.manage</c>): channels and their settings, the inbound authentication
/// (API key shown once at its rotation, client certificate thumbprint, allowed addresses) and the mappings. A change
/// that alters what a connected channel must know (connection, mappings) queues a full resynchronization in the same
/// transaction.
/// </summary>
public sealed class ChannelAdministrationService
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _user;
    private readonly IReferentialQueries _referential;
    private readonly IFleetQueries _fleet;
    private readonly IPricingQueries _pricing;
    private readonly ChannelAdapterRegistry _adapters;
    private readonly ChannelSyncQueue _queue;
    private readonly ChannelSyncSignal _signal;
    private readonly TimeProvider _clock;

    internal ChannelAdministrationService(
        IAppDbContext db,
        IUnitOfWork unitOfWork,
        IUserContext user,
        IReferentialQueries referential,
        IFleetQueries fleet,
        IPricingQueries pricing,
        ChannelAdapterRegistry adapters,
        ChannelSyncQueue queue,
        ChannelSyncSignal signal,
        TimeProvider clock)
    {
        _db = db;
        _unitOfWork = unitOfWork;
        _user = user;
        _referential = referential;
        _fleet = fleet;
        _pricing = pricing;
        _adapters = adapters;
        _queue = queue;
        _signal = signal;
        _clock = clock;
    }

    private bool CanManage => _user.HasPermission(Permissions.ChannelsManage);

    /// <summary>The adapters a channel can be given in this environment (the test adapter only in Development and Testing).</summary>
    public IReadOnlyList<ChannelAdapterKind> AvailableAdapters => _adapters.AvailableKinds;

    public async Task<Result<IReadOnlyList<ChannelListItem>>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        var agencies = (await _referential.GetAgenciesAsync(cancellationToken)).ToDictionary(a => a.Id, a => a.Name);
        var channels = await _db.Set<Channel>().AsNoTracking().Include(c => c.Mappings).OrderBy(c => c.Name).ToListAsync(cancellationToken);
        var reminders = await _db.Set<ChannelReplicationItem>().AsNoTracking()
            .Where(r => r.AcknowledgedAtUtc == null)
            .GroupBy(r => r.ChannelId)
            .Select(g => new { ChannelId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ChannelId, g => g.Count, cancellationToken);
        var failed = await _db.Set<ChannelSyncItem>().AsNoTracking()
            .Where(i => i.Status == ChannelSyncStatus.Failed)
            .GroupBy(i => i.ChannelId)
            .Select(g => new { ChannelId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ChannelId, g => g.Count, cancellationToken);
        return channels.Select(c => new ChannelListItem(
                c.Id,
                c.AgencyId,
                agencies.GetValueOrDefault(c.AgencyId) ?? string.Empty,
                c.Code,
                c.Name,
                c.SalesChannel,
                c.State,
                c.AdapterKind,
                c.DefaultPaymentMode,
                c.CommissionRate,
                c.ApiKeyHash is not null,
                c.ClientCertificateThumbprint is not null,
                c.IpAllowlist.Ranges.Count,
                c.Mappings.Count(m => m.Kind == ChannelMappingKind.Category),
                c.Mappings.Count(m => m.Kind == ChannelMappingKind.Location),
                reminders.GetValueOrDefault(c.Id),
                failed.GetValueOrDefault(c.Id)))
            .ToList();
    }

    public async Task<Result<ChannelDetailView>> GetAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        var channel = await _db.Set<Channel>().AsNoTracking().Include(c => c.Mappings).FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return ChannelsErrors.NotFound;
        }

        var agency = (await _referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Id == channel.AgencyId);
        var categories = await _fleet.GetCategoriesAsync(channel.AgencyId, includeInactive: true, cancellationToken);
        var locations = await _referential.GetLocationsAsync(channel.AgencyId, cancellationToken);
        var extras = await _pricing.GetExtrasAsync(channel.AgencyId, channel.SalesChannel, cancellationToken);
        var extraNames = extras.GroupBy(e => e.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        return new ChannelDetailView(
            channel.Id,
            channel.AgencyId,
            agency?.Name ?? string.Empty,
            channel.Code,
            channel.Name,
            channel.SalesChannel,
            channel.State,
            channel.AdapterKind,
            _adapters.AvailableKinds,
            channel.DefaultPaymentMode,
            channel.CommissionRate,
            channel.ApiKeyHint,
            channel.ApiKeyCreatedAtUtc,
            channel.ClientCertificateThumbprint,
            [.. channel.IpAllowlist.Ranges.Select(r => r.ToString())],
            [
                .. categories.Select(c =>
                {
                    var mapping = channel.MappingOf(ChannelMappingKind.Category, c.Id);
                    return new CategoryMappingView(c.Id, c.AcrissCode, c.Name, c.IsActive, mapping?.Id, mapping?.ChannelCode, mapping?.AcrissCode);
                }),
            ],
            [
                .. locations.Select(l =>
                {
                    var mapping = channel.MappingOf(ChannelMappingKind.Location, l.Id);
                    return new LocationMappingView(l.Id, l.Code, l.Name, mapping?.Id, mapping?.ChannelCode);
                }),
            ],
            [
                .. channel.Mappings
                    .Where(m => !m.IsById)
                    .OrderBy(m => m.Kind)
                    .ThenBy(m => m.InternalCode, StringComparer.Ordinal)
                    .Select(m => new OptionMappingView(m.Id, m.Kind, m.InternalCode!, extraNames.GetValueOrDefault(m.InternalCode!), m.ChannelCode)),
            ],
            [.. extraNames.Select(pair => new MappableExtraView(pair.Key, pair.Value)).OrderBy(e => e.Code, StringComparer.Ordinal)],
            channel.UpdatedAtUtc,
            channel.UpdatedBy);
    }

    public async Task<Result<Guid>> CreateAsync(CreateChannelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        if (!(await _referential.GetAgenciesAsync(cancellationToken)).Any(a => a.Id == request.AgencyId))
        {
            return ChannelsErrors.AgencyNotFound;
        }

        if (!_adapters.IsAvailable(request.AdapterKind))
        {
            return ChannelsErrors.AdapterUnavailable;
        }

        var created = Channel.Create(request.AgencyId, request.Code, request.Name, request.SalesChannel, request.AdapterKind, request.DefaultPaymentMode, request.CommissionRate, Stamp());
        if (created.IsFailure)
        {
            return created.Error;
        }

        var channel = created.Value;
        try
        {
            return await _unitOfWork.ExecuteAsync<Guid>(
                async ct =>
                {
                    var taken = await TakenAsync(channel, ct);
                    if (taken is not null)
                    {
                        return taken;
                    }

                    _db.Set<Channel>().Add(channel);
                    return channel.Id;
                },
                cancellationToken: cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent creation won a unique index: report which rule it broke.
            return await TakenAsync(channel, cancellationToken) ?? throw new InvalidOperationException("The channel could not be created.");
        }
    }

    /// <summary>Name, state, adapter, payment mode and commission; connecting a channel queues its full resynchronization.</summary>
    public async Task<Result> UpdateSettingsAsync(Guid channelId, UpdateChannelSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        if (!_adapters.IsAvailable(request.AdapterKind))
        {
            return ChannelsErrors.AdapterUnavailable;
        }

        return await ChangeAsync(
            channelId,
            async (channel, stamp, ct) =>
            {
                var wasConnected = channel.State == ChannelState.Connected;
                var outcome = Result.Combine(
                    channel.UpdateSettings(request.Name, request.DefaultPaymentMode, request.CommissionRate, stamp),
                    request.State == ChannelState.Connected
                        ? Result.Combine(channel.ChangeAdapter(request.AdapterKind, stamp), channel.ChangeState(request.State, stamp))
                        : Result.Combine(channel.ChangeState(request.State, stamp), channel.ChangeAdapter(request.AdapterKind, stamp)));
                if (outcome.IsSuccess && !wasConnected && channel.State == ChannelState.Connected)
                {
                    await _queue.EnqueueAsync(channel.Id, ChannelSyncOperation.FullResync, null, "connected", ct);
                }

                return outcome;
            },
            cancellationToken);
    }

    /// <summary>A new inbound API key, returned once: only its hash is kept, the previous key stops working at once.</summary>
    public async Task<Result<string>> RotateApiKeyAsync(Guid channelId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        string? key = null;
        var rotated = await ChangeAsync(
            channelId,
            (channel, stamp, _) =>
            {
                key = channel.RotateApiKey(stamp).Key;
                return Task.FromResult(Result.Success());
            },
            cancellationToken);
        return rotated.IsSuccess ? key! : rotated.Error;
    }

    public Task<Result> RevokeApiKeyAsync(Guid channelId, CancellationToken cancellationToken = default) =>
        CanManage
            ? ChangeAsync(
                channelId,
                (channel, stamp, _) =>
                {
                    channel.RevokeApiKey(stamp);
                    return Task.FromResult(Result.Success());
                },
                cancellationToken)
            : Task.FromResult<Result>(ChannelsErrors.Forbidden);

    /// <summary>The client certificate thumbprint (empty: none) and the allowed addresses (CIDR, one per line or separated by <c>;</c>).</summary>
    public Task<Result> SetInboundSecurityAsync(Guid channelId, string? certificateThumbprint, string? allowedIpRanges, CancellationToken cancellationToken = default) =>
        CanManage
            ? ChangeAsync(
                channelId,
                (channel, stamp, _) => Task.FromResult(Result.Combine(channel.SetClientCertificate(certificateThumbprint, stamp), channel.SetAllowedIpRanges(allowedIpRanges, stamp))),
                cancellationToken)
            : Task.FromResult<Result>(ChannelsErrors.Forbidden);

    public async Task<Result> MapCategoryAsync(Guid channelId, Guid categoryId, string? channelCode, string? acrissCode, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        var category = await _fleet.GetCategoryAsync(categoryId, cancellationToken);
        return await ChangeAsync(
            channelId,
            async (channel, stamp, ct) =>
            {
                if (category is null || category.AgencyId != channel.AgencyId)
                {
                    return ChannelsErrors.MappingCategoryNotFound;
                }

                var mapped = channel.MapCategory(categoryId, channelCode, acrissCode, stamp);
                return mapped.IsFailure ? mapped.Error : await ResyncAsync(channel, ct);
            },
            cancellationToken);
    }

    public async Task<Result> MapLocationAsync(Guid channelId, Guid locationId, string? channelCode, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        return await ChangeAsync(
            channelId,
            async (channel, stamp, ct) =>
            {
                var locations = await _referential.GetLocationsAsync(channel.AgencyId, ct);
                if (locations.All(l => l.Id != locationId))
                {
                    return ChannelsErrors.MappingLocationNotFound;
                }

                var mapped = channel.MapLocation(locationId, channelCode, stamp);
                return mapped.IsFailure ? mapped.Error : await ResyncAsync(channel, ct);
            },
            cancellationToken);
    }

    /// <summary>Maps an extra, insurance or fee code of the rate plan to the OpenTravel code entered by the agency.</summary>
    public Task<Result> MapOptionAsync(Guid channelId, ChannelMappingKind kind, string? internalCode, string? channelCode, CancellationToken cancellationToken = default) =>
        CanManage
            ? ChangeAsync(
                channelId,
                async (channel, stamp, ct) =>
                {
                    var mapped = channel.MapOption(kind, internalCode, channelCode, stamp);
                    return mapped.IsFailure ? mapped.Error : await ResyncAsync(channel, ct);
                },
                cancellationToken)
            : Task.FromResult<Result>(ChannelsErrors.Forbidden);

    public Task<Result> RemoveMappingAsync(Guid channelId, Guid mappingId, CancellationToken cancellationToken = default) =>
        CanManage
            ? ChangeAsync(
                channelId,
                async (channel, stamp, ct) =>
                {
                    var removed = channel.RemoveMapping(mappingId, stamp);
                    return removed.IsFailure ? removed : await ResyncAsync(channel, ct);
                },
                cancellationToken)
            : Task.FromResult<Result>(ChannelsErrors.Forbidden);

    private ChannelStamp Stamp() => new(_clock.GetUtcNow(), _user.UserName);

    /// <summary>A connected channel resends everything once its mappings change.</summary>
    private async Task<Result> ResyncAsync(Channel channel, CancellationToken cancellationToken)
    {
        if (channel.State == ChannelState.Connected)
        {
            await _queue.EnqueueAsync(channel.Id, ChannelSyncOperation.FullResync, null, "mapping", cancellationToken);
        }

        return Result.Success();
    }

    private async Task<Error?> TakenAsync(Channel channel, CancellationToken cancellationToken)
    {
        if (await _db.Set<Channel>().AnyAsync(c => c.Code == channel.Code && c.Id != channel.Id, cancellationToken))
        {
            return ChannelsErrors.CodeTaken;
        }

        return await _db.Set<Channel>().AnyAsync(c => c.AgencyId == channel.AgencyId && c.SalesChannel == channel.SalesChannel && c.Id != channel.Id, cancellationToken)
            ? ChannelsErrors.SalesChannelTaken
            : null;
    }

    /// <summary>Loads the channel (tracked, with its mappings) in a unit of work, applies the change, wakes the pump after the commit.</summary>
    private async Task<Result> ChangeAsync(Guid channelId, Func<Channel, ChannelStamp, CancellationToken, Task<Result>> change, CancellationToken cancellationToken)
    {
        Result outcome;
        try
        {
            outcome = await _unitOfWork.ExecuteAsync(
                async ct =>
                {
                    var channel = await _db.Set<Channel>().Include(c => c.Mappings).FirstOrDefaultAsync(c => c.Id == channelId, ct);
                    return channel is null ? ChannelsErrors.NotFound : await change(channel, Stamp(), ct);
                },
                cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ChannelsErrors.ConcurrentUpdate;
        }
        catch (DbUpdateException)
        {
            // Two people mapped the same code at once: the unique index of the mappings refused the second.
            return ChannelsErrors.MappingCodeTaken;
        }

        if (outcome.IsSuccess)
        {
            _signal.Wake();
        }

        return outcome;
    }
}
