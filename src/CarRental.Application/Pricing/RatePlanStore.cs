using CarRental.Application.Common.Persistence;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Pricing;

/// <summary>
/// Loads and adds rate plans with their whole content (module-internal port, ADR-0024): the aggregate spans eight child
/// collections, so loading lives in one place, and the services can be tested with an in-memory store.
/// </summary>
internal interface IRatePlanStore
{
    /// <summary>A plan with its content, tracked by the scope (changes are saved by the unit of work).</summary>
    Task<RatePlan?> FindAsync(Guid planId, CancellationToken cancellationToken);

    /// <summary>The published plan of an agency for a channel (<see langword="null"/>: the default plan), with its content.</summary>
    Task<RatePlan?> FindPublishedAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken);

    Task<bool> DraftExistsAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken);

    /// <summary>The agency of a plan, read without tracking; <see langword="null"/> when the plan does not exist.</summary>
    Task<Guid?> FindAgencyIdAsync(Guid planId, CancellationToken cancellationToken);

    /// <summary>The highest version ever published for the agency and channel (0 when none).</summary>
    Task<int> GetLastVersionAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken);

    /// <summary>Plans (header only, no content) of an agency, or of every agency; archived plans only when asked.</summary>
    Task<IReadOnlyList<RatePlan>> ListAsync(Guid? agencyId, bool includeArchived, CancellationToken cancellationToken);

    /// <summary>Every plan (header only) of an agency and channel: its drafts, then its versions, newest first.</summary>
    Task<IReadOnlyList<RatePlan>> ListVersionsAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken);

    /// <summary>Whether any plan exists for the agency (idempotent demo seeding).</summary>
    Task<bool> AnyForAgencyAsync(Guid agencyId, CancellationToken cancellationToken);

    void Add(RatePlan plan);
}

/// <summary>
/// <see cref="IRatePlanStore"/> on the single EF Core context. The content is loaded collection by collection (one query
/// each, attached to the tracked plan by EF fix-up): a single query including eight collections would multiply their rows.
/// </summary>
internal sealed class EfRatePlanStore(IAppDbContext db) : IRatePlanStore
{
    public async Task<RatePlan?> FindAsync(Guid planId, CancellationToken cancellationToken) =>
        await WithContentAsync(await db.Set<RatePlan>().FirstOrDefaultAsync(p => p.Id == planId, cancellationToken), cancellationToken);

    public async Task<RatePlan?> FindPublishedAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken)
    {
        var plan = await db.Set<RatePlan>().FirstOrDefaultAsync(
            p => p.AgencyId == agencyId && p.Channel == channel && p.Status == RatePlanStatus.Published,
            cancellationToken);
        return await WithContentAsync(plan, cancellationToken);
    }

    public Task<bool> DraftExistsAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken) =>
        db.Set<RatePlan>().AnyAsync(p => p.AgencyId == agencyId && p.Channel == channel && p.Status == RatePlanStatus.Draft, cancellationToken);

    public Task<Guid?> FindAgencyIdAsync(Guid planId, CancellationToken cancellationToken) =>
        db.Set<RatePlan>().AsNoTracking().Where(p => p.Id == planId).Select(p => (Guid?)p.AgencyId).FirstOrDefaultAsync(cancellationToken);

    public async Task<int> GetLastVersionAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken) =>
        await db.Set<RatePlan>()
            .Where(p => p.AgencyId == agencyId && p.Channel == channel)
            .MaxAsync(p => p.Version, cancellationToken) ?? 0;

    public async Task<IReadOnlyList<RatePlan>> ListAsync(Guid? agencyId, bool includeArchived, CancellationToken cancellationToken)
    {
        var query = db.Set<RatePlan>().AsNoTracking();
        if (agencyId is { } id)
        {
            query = query.Where(p => p.AgencyId == id);
        }

        if (!includeArchived)
        {
            query = query.Where(p => p.Status != RatePlanStatus.Archived);
        }

        return await query
            .OrderBy(p => p.AgencyId)
            .ThenBy(p => p.Channel)
            .ThenBy(p => p.Status)
            .ThenByDescending(p => p.Version)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RatePlan>> ListVersionsAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken) =>
        await db.Set<RatePlan>()
            .AsNoTracking()
            .Where(p => p.AgencyId == agencyId && p.Channel == channel)
            .OrderBy(p => p.Version == null ? 0 : 1)
            .ThenByDescending(p => p.Version)
            .ThenByDescending(p => p.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public Task<bool> AnyForAgencyAsync(Guid agencyId, CancellationToken cancellationToken) =>
        db.Set<RatePlan>().AnyAsync(p => p.AgencyId == agencyId, cancellationToken);

    public void Add(RatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        db.Set<RatePlan>().Add(plan);
    }

    private async Task<RatePlan?> WithContentAsync(RatePlan? plan, CancellationToken cancellationToken)
    {
        if (plan is null)
        {
            return null;
        }

        var id = plan.Id;
        await db.Set<Season>().Where(s => s.RatePlanId == id).LoadAsync(cancellationToken);
        await db.Set<PriorityEvent>().Where(e => e.RatePlanId == id).LoadAsync(cancellationToken);
        await db.Set<CategoryPrice>().Where(p => p.RatePlanId == id).LoadAsync(cancellationToken);
        await db.Set<DurationTier>().Where(t => t.RatePlanId == id).LoadAsync(cancellationToken);
        await db.Set<TierCategoryDiscount>().Where(d => d.RatePlanId == id).LoadAsync(cancellationToken);
        await db.Set<Extra>().Where(e => e.RatePlanId == id).LoadAsync(cancellationToken);
        await db.Set<Promotion>().Where(p => p.RatePlanId == id).LoadAsync(cancellationToken);
        await db.Set<ChannelRule>().Where(r => r.RatePlanId == id).LoadAsync(cancellationToken);
        return plan;
    }
}
