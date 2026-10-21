using CarRental.Application.Channels.Adapters;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Channels.Core;

/// <summary>An immutable view of a channel for one operation: identity, settings and mappings (both ways).</summary>
public sealed record ChannelContext(
    Guid Id,
    Guid AgencyId,
    string Code,
    string Name,
    SalesChannel SalesChannel,
    ChannelState State,
    ChannelAdapterKind AdapterKind,
    ChannelPaymentMode DefaultPaymentMode,
    decimal CommissionRate,
    ChannelMappingSet Mappings)
{
    public ChannelPushTarget Target => new(Id, Code, SalesChannel);
}

/// <summary>Loads <see cref="ChannelContext"/>s: the channel and its mappings, plus the ACRISS codes of the agency's categories.</summary>
internal sealed class ChannelContextLoader(IAppDbContext db, IFleetQueries fleet)
{
    public async Task<ChannelContext?> ByIdAsync(Guid channelId, CancellationToken cancellationToken)
    {
        var channel = await db.Set<Channel>().AsNoTracking().Include(c => c.Mappings).FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        return channel is null ? null : await ToContextAsync(channel, cancellationToken);
    }

    /// <summary>The channel of an inbound address segment (case ignored), or <see langword="null"/>.</summary>
    public async Task<Channel?> FindByCodeAsync(string code, CancellationToken cancellationToken)
    {
        var normalized = ChannelCodes.ChannelCode(code);
        return normalized.IsFailure
            ? null
            : await db.Set<Channel>().AsNoTracking().Include(c => c.Mappings).FirstOrDefaultAsync(c => c.Code == normalized.Value, cancellationToken);
    }

    public async Task<ChannelContext> ToContextAsync(Channel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var categories = await fleet.GetCategoriesAsync(channel.AgencyId, includeInactive: true, cancellationToken);
        var mappings = new ChannelMappingSet(
            channel.Mappings.Select(m => new ChannelMappingEntry(m.Id, m.Kind, m.InternalId, m.InternalCode, m.ChannelCode, m.AcrissCode)),
            categories.ToDictionary(c => c.Id, c => c.AcrissCode));
        return new ChannelContext(
            channel.Id,
            channel.AgencyId,
            channel.Code,
            channel.Name,
            channel.SalesChannel,
            channel.State,
            channel.AdapterKind,
            channel.DefaultPaymentMode,
            channel.CommissionRate,
            mappings);
    }
}
