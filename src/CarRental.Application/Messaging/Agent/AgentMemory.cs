using CarRental.Application.Common.Persistence;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Messaging.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CarRental.Application.Messaging.Agent;

/// <summary>
/// The memory of the agent for one turn, in a DI scope of its own (its own unit of work): the state of the conversation
/// (created at its first turn) and the turn record of the cost reports, saved together at the end of the turn.
/// </summary>
internal sealed class AgentMemory(IAppDbContext db)
{
    public async Task<AgentConversationState> LoadAsync(Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = await db.Set<AgentConversationState>().FirstOrDefaultAsync(s => s.Id == conversationId, cancellationToken);
        if (state is not null)
        {
            return state;
        }

        state = AgentConversationState.Start(conversationId, now);
        db.Set<AgentConversationState>().Add(state);
        return state;
    }

    public async Task SaveAsync(AgentTurnRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        db.Set<AgentTurnRecord>().Add(record);
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// The agency the agent presents: <see cref="AgentSiteOptions.AgencyId"/> when configured, else the only active agency,
/// else the first one created (the same rule as the public site), with its places, opening hours and active categories.
/// </summary>
internal sealed class AgentAgencyLoader(IReferentialQueries referential, IFleetQueries fleet, ISettingsReader settings, IOptions<AgentSiteOptions> site)
{
    private AgentAgencyContext? _loaded;

    public async Task<AgentAgencyContext?> LoadAsync(CancellationToken cancellationToken)
    {
        if (_loaded is not null)
        {
            return _loaded;
        }

        var agencies = (await referential.GetAgenciesAsync(cancellationToken)).Where(a => a.IsActive).ToList();
        var agency = site.Value.AgencyId is { } id ? agencies.FirstOrDefault(a => a.Id == id) : agencies.OrderBy(a => a.Id).FirstOrDefault();
        if (agency is null)
        {
            return null;
        }

        var locations = await referential.GetLocationsAsync(agency.Id, cancellationToken);
        var hours = await settings.GetAsync(PricingSettings.AgencyOpeningHours, agency.Id, cancellationToken);
        var categories = await fleet.GetCategoriesAsync(agency.Id, includeInactive: false, cancellationToken);
        _loaded = new AgentAgencyContext(agency, locations, hours, categories);
        return _loaded;
    }
}
