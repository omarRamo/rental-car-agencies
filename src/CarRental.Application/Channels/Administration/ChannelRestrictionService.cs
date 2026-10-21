using CarRental.Application.Channels.Outbound;
using CarRental.Application.Common.Availability;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Channels.Administration;

/// <summary>
/// Quotas and stop-sells of the channels (SPEC §7, permission <c>channels.manage</c>). A change applies at once to the
/// availability engine (every reader and writer, through <c>IChannelAvailabilityPolicy</c>); in the same transaction
/// it queues the availability update of a connected channel, or a reminder to replicate on the extranet of a manual
/// channel.
/// </summary>
public sealed class ChannelRestrictionService
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _user;
    private readonly IFleetQueries _fleet;
    private readonly IReferentialQueries _referential;
    private readonly ChannelSyncQueue _queue;
    private readonly ChannelSyncSignal _signal;
    private readonly IAvailabilityChangeNotifier _availability;
    private readonly TimeProvider _clock;

    internal ChannelRestrictionService(
        IAppDbContext db,
        IUnitOfWork unitOfWork,
        IUserContext user,
        IFleetQueries fleet,
        IReferentialQueries referential,
        ChannelSyncQueue queue,
        ChannelSyncSignal signal,
        IAvailabilityChangeNotifier availability,
        TimeProvider clock)
    {
        _db = db;
        _unitOfWork = unitOfWork;
        _user = user;
        _fleet = fleet;
        _referential = referential;
        _queue = queue;
        _signal = signal;
        _availability = availability;
        _clock = clock;
    }

    private bool CanManage => _user.HasPermission(Permissions.ChannelsManage);

    /// <summary>The restrictions of a channel that end on <paramref name="since"/> or later (today when omitted), by start.</summary>
    public async Task<Result<IReadOnlyList<RestrictionView>>> ListAsync(Guid channelId, DateOnly? since = null, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        var channel = await _db.Set<Channel>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return ChannelsErrors.NotFound;
        }

        var from = since ?? TunisTime.DateOf(_clock.GetUtcNow());
        var quotas = await _db.Set<ChannelQuota>().AsNoTracking()
            .Where(q => q.ChannelId == channelId && q.EndDate >= from)
            .OrderBy(q => q.StartDate)
            .ThenBy(q => q.Kind)
            .ToListAsync(cancellationToken);
        var categories = (await _fleet.GetCategoriesAsync(channel.AgencyId, includeInactive: true, cancellationToken)).ToDictionary(c => c.Id, c => c.AcrissCode);
        var locations = (await _referential.GetLocationsAsync(channel.AgencyId, cancellationToken)).ToDictionary(l => l.Id, l => l.Code);
        return quotas.Select(q => new RestrictionView(
                q.Id,
                q.ChannelId,
                q.Kind,
                q.CategoryId,
                categories.GetValueOrDefault(q.CategoryId) ?? "?",
                q.LocationId,
                q.LocationId is { } location ? locations.GetValueOrDefault(location) ?? "?" : null,
                q.StartDate,
                q.EndDate,
                q.ReservedVehicles,
                q.Note,
                q.UpdatedAtUtc,
                q.UpdatedBy))
            .ToList();
    }

    public async Task<Result<Guid>> AddAsync(Guid channelId, RestrictionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        var categoryId = request.CategoryId;
        var result = await RunAsync<Guid>(
            async ct =>
            {
                var channel = await _db.Set<Channel>().FirstOrDefaultAsync(c => c.Id == channelId, ct);
                if (channel is null)
                {
                    return ChannelsErrors.NotFound;
                }

                var targets = await CheckTargetsAsync(channel, request.CategoryId, request.LocationId, ct);
                if (targets.IsFailure)
                {
                    return targets.Error;
                }

                var stamp = Stamp();
                var created = request.Kind == ChannelQuotaKind.StopSell
                    ? ChannelQuota.StopSell(channel, request.CategoryId, request.LocationId, request.StartDate, request.EndDate, request.Note, stamp)
                    : ChannelQuota.Reserve(channel, request.CategoryId, request.StartDate, request.EndDate, request.ReservedVehicles, request.Note, stamp);
                if (created.IsFailure)
                {
                    return created.Error;
                }

                _db.Set<ChannelQuota>().Add(created.Value);
                await FollowAsync(channel, created.Value, created.Value.Kind == ChannelQuotaKind.StopSell ? ChannelReplicationKind.StopSellAdded : ChannelReplicationKind.QuotaAdded, stamp, ct);
                return created.Value.Id;
            },
            cancellationToken);
        After(result, categoryId);
        return result;
    }

    public async Task<Result> ChangeAsync(Guid restrictionId, RestrictionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        Guid? categoryId = null;
        var result = await RunAsync<bool>(
            async ct =>
            {
                var quota = await _db.Set<ChannelQuota>().FirstOrDefaultAsync(q => q.Id == restrictionId, ct);
                var channel = quota is null ? null : await _db.Set<Channel>().FirstOrDefaultAsync(c => c.Id == quota.ChannelId, ct);
                if (quota is null || channel is null)
                {
                    return ChannelsErrors.RestrictionNotFound;
                }

                var targets = await CheckTargetsAsync(channel, quota.CategoryId, request.LocationId, ct);
                if (targets.IsFailure)
                {
                    return targets.Error;
                }

                var stamp = Stamp();
                var changed = quota.Change(request.LocationId, request.StartDate, request.EndDate, request.ReservedVehicles, request.Note, stamp);
                if (changed.IsFailure)
                {
                    return changed.Error;
                }

                categoryId = quota.CategoryId;
                await FollowAsync(channel, quota, quota.Kind == ChannelQuotaKind.StopSell ? ChannelReplicationKind.StopSellChanged : ChannelReplicationKind.QuotaChanged, stamp, ct);
                return true;
            },
            cancellationToken);
        After(result, categoryId);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    public async Task<Result> RemoveAsync(Guid restrictionId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        Guid? categoryId = null;
        var result = await RunAsync<bool>(
            async ct =>
            {
                var quota = await _db.Set<ChannelQuota>().FirstOrDefaultAsync(q => q.Id == restrictionId, ct);
                var channel = quota is null ? null : await _db.Set<Channel>().FirstOrDefaultAsync(c => c.Id == quota.ChannelId, ct);
                if (quota is null || channel is null)
                {
                    return ChannelsErrors.RestrictionNotFound;
                }

                var stamp = Stamp();
                quota.MarkRemoved(stamp);
                _db.Set<ChannelQuota>().Remove(quota);
                categoryId = quota.CategoryId;
                await FollowAsync(channel, quota, quota.Kind == ChannelQuotaKind.StopSell ? ChannelReplicationKind.StopSellRemoved : ChannelReplicationKind.QuotaRemoved, stamp, ct);
                return true;
            },
            cancellationToken);
        After(result, categoryId);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    private ChannelStamp Stamp() => new(_clock.GetUtcNow(), _user.UserName);

    private async Task<Result> CheckTargetsAsync(Channel channel, Guid categoryId, Guid? locationId, CancellationToken cancellationToken)
    {
        var category = await _fleet.GetCategoryAsync(categoryId, cancellationToken);
        if (category is null || category.AgencyId != channel.AgencyId)
        {
            return ChannelsErrors.RestrictionCategoryNotFound;
        }

        if (locationId is { } location && (await _referential.GetLocationsAsync(channel.AgencyId, cancellationToken)).All(l => l.Id != location))
        {
            return ChannelsErrors.RestrictionLocationNotFound;
        }

        return Result.Success();
    }

    /// <summary>What the channel must learn of the change: an update (connected) or a reminder (manual).</summary>
    private async Task FollowAsync(Channel channel, ChannelQuota quota, ChannelReplicationKind kind, ChannelStamp stamp, CancellationToken cancellationToken)
    {
        switch (channel.State)
        {
            case ChannelState.Connected:
                await _queue.EnqueueAsync(channel.Id, ChannelSyncOperation.PushAvailability, quota.CategoryId, "restriction", cancellationToken);
                break;
            case ChannelState.Manual:
                _db.Set<ChannelReplicationItem>().Add(ChannelReplicationItem.ForRestriction(quota, kind, stamp));
                break;
        }
    }

    /// <summary>After the commit: the availability engine reloads the category, the pump sends the update.</summary>
    private void After(Result result, Guid? categoryId)
    {
        if (result.IsFailure)
        {
            return;
        }

        if (categoryId is { } id)
        {
            _availability.CategoryChanged(id);
        }

        _signal.Wake();
    }

    private async Task<Result<T>> RunAsync<T>(Func<CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _unitOfWork.ExecuteAsync(operation, cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ChannelsErrors.ConcurrentUpdate;
        }
    }
}
