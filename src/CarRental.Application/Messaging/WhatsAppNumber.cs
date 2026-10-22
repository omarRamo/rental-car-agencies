using System.Collections.Concurrent;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Webhooks;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging;

/// <summary>
/// <see cref="IWhatsAppNumber"/> over the routing of the inbound messages (<see cref="WhatsAppAgencyRouting"/>), so that
/// both directions follow one rule: WhatsApp is open for an agency only while the platform's number serves it. The
/// pipeline asks here before any send or bot turn; an agency whose module is on but that the number does not serve gets
/// nothing, and a warning says so once per agency and run of the application (again after it was served meanwhile), not
/// per message.
/// </summary>
internal sealed partial class WhatsAppNumber(WhatsAppAgencyRouting routing, IAgencyDirectory agencies, ILogger<WhatsAppNumber> logger) : IWhatsAppNumber
{
    private readonly ConcurrentDictionary<Guid, byte> _warned = new();

    public async Task<Guid?> ServedAgencyAsync(CancellationToken cancellationToken = default) =>
        (await routing.ResolveAsync(cancellationToken)).AgencyId;

    public async Task<bool> ServesAsync(Guid agencyId, CancellationToken cancellationToken = default) =>
        await ServedAgencyAsync(cancellationToken) == agencyId;

    public Task<WhatsAppNumberOutlook> OutlookAsync(Guid agencyId, CancellationToken cancellationToken = default) =>
        routing.OutlookAsync(agencyId, cancellationToken);

    /// <summary>Whether WhatsApp is open for the agency: the number serves it (the agency is then active with the module on).</summary>
    public async Task<bool> IsOpenAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        var route = await routing.ResolveAsync(cancellationToken);
        if (route.AgencyId == agencyId)
        {
            _warned.TryRemove(agencyId, out _);
            return true;
        }

        if (await agencies.HasModuleAsync(agencyId, AgencyModules.WhatsApp, cancellationToken))
        {
            WarnNotServed(agencyId, route);
        }

        return false;
    }

    /// <summary>The agencies WhatsApp is open for: the one the number serves, if any.</summary>
    public async Task<IReadOnlyList<Guid>> OpenAgenciesAsync(CancellationToken cancellationToken)
    {
        var route = await routing.ResolveAsync(cancellationToken);
        foreach (var agency in await agencies.ListAsync(cancellationToken))
        {
            if (agency.Id == route.AgencyId)
            {
                _warned.TryRemove(agency.Id, out _);
            }
            else if (agency.IsActive && agency.HasModule(AgencyModules.WhatsApp))
            {
                WarnNotServed(agency.Id, route);
            }
        }

        return route.AgencyId is { } served ? [served] : [];
    }

    private void WarnNotServed(Guid agencyId, WhatsAppRoute route)
    {
        if (_warned.TryAdd(agencyId, 0))
        {
            LogNotServed(agencyId, route.AgencyId?.ToString() ?? $"no agency ({route.Status})");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The WhatsApp module of the agency {AgencyId} is on, but the platform's WhatsApp number serves {ServedAgency}: nothing is sent nor received for it (notifications, inbox, bot) until WhatsApp:AgencyId designates it.")]
    private partial void LogNotServed(Guid agencyId, string servedAgency);
}
