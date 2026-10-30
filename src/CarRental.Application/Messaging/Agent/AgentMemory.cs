using CarRental.Application.Common.Persistence;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Messaging.Agent;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Messaging.Agent;

/// <summary>
/// The memory of the agent for one turn, in a DI scope of its own working for the agency of the conversation (its own
/// unit of work): the state of the conversation (created at its first turn) and the turn record of the cost reports,
/// saved together at the end of the turn.
/// </summary>
internal sealed class AgentMemory(IAppDbContext db)
{
    public async Task<AgentConversationState> LoadAsync(Guid conversationId, Guid agencyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = await db.Set<AgentConversationState>().FirstOrDefaultAsync(s => s.Id == conversationId, cancellationToken);
        if (state is not null)
        {
            return state;
        }

        state = AgentConversationState.Start(conversationId, agencyId, now);
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
/// The agency the agent presents: the agency of the conversation (ADR-0067), when it is active, with its places, opening
/// hours and active categories — never another agency's.
/// </summary>
internal sealed class AgentAgencyLoader(IReferentialQueries referential, IFleetQueries fleet, ISettingsReader settings, IAgencyDirectory directory)
{
    private AgentAgencyContext? _loaded;

    public async Task<AgentAgencyContext?> LoadAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        if (_loaded is { } loaded && loaded.Agency.Id == agencyId)
        {
            return loaded;
        }

        var agency = (await referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Id == agencyId && a.IsActive);
        if (agency is null || await directory.FindAsync(agencyId, cancellationToken) is not { } site)
        {
            return null;
        }

        var locations = await referential.GetLocationsAsync(agency.Id, cancellationToken);
        var hours = await settings.GetAsync(PricingSettings.AgencyOpeningHours, agency.Id, cancellationToken);
        var categories = await fleet.GetCategoriesAsync(agency.Id, includeInactive: false, cancellationToken);
        _loaded = new AgentAgencyContext(agency, locations, hours, categories, site.Slug, site.PrimaryHost);
        return _loaded;
    }
}
