using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Pricing.Administration;

/// <summary>
/// Back-office administration of rate plans (<c>/admin/pricing</c>): list, read, create a draft (from the published plan
/// or any version), edit a draft section by section, validate, publish (next version, the replaced plan is archived),
/// archive, and simulate a quote with the published plans and a draft side by side.
/// Reading and simulating need <c>pricing.read</c>; every change needs <c>pricing.manage</c>.
/// </summary>
public sealed class RatePlanAdministrationService
{
    /// <summary>Name of a first, empty draft when none is given.</summary>
    public const string DefaultPlanName = "Grille tarifaire";

    private readonly IRatePlanStore _store;
    private readonly QuoteCalculator _calculator;
    private readonly IReferentialQueries _referential;
    private readonly IFleetQueries _fleet;
    private readonly IUserContext _user;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _clock;

    internal RatePlanAdministrationService(
        IRatePlanStore store,
        QuoteCalculator calculator,
        IReferentialQueries referential,
        IFleetQueries fleet,
        IUserContext user,
        IUnitOfWork unitOfWork,
        TimeProvider clock)
    {
        _store = store;
        _calculator = calculator;
        _referential = referential;
        _fleet = fleet;
        _user = user;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    /// <summary>
    /// Plans of an agency (or of every agency the operation sees: its own for the staff, ADR-0067), drafts and published
    /// plans first; archives on demand.
    /// </summary>
    public async Task<Result<IReadOnlyList<RatePlanListItem>>> ListAsync(
        Guid? agencyId,
        bool includeArchived,
        CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.PricingRead))
        {
            return PricingErrors.Forbidden;
        }

        var plans = await _store.ListAsync(agencyId, includeArchived, cancellationToken);
        return Result.Success<IReadOnlyList<RatePlanListItem>>([.. plans.Select(RatePlanMapper.ToListItem)]);
    }

    /// <summary>Every version and draft of an agency and channel, newest first (version history).</summary>
    public async Task<Result<IReadOnlyList<RatePlanListItem>>> GetHistoryAsync(
        Guid agencyId,
        SalesChannel? channel,
        CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.PricingRead))
        {
            return PricingErrors.Forbidden;
        }

        var plans = await _store.ListVersionsAsync(agencyId, channel, cancellationToken);
        return Result.Success<IReadOnlyList<RatePlanListItem>>([.. plans.Select(RatePlanMapper.ToListItem)]);
    }

    public async Task<Result<RatePlanDetail>> GetAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.PricingRead))
        {
            return PricingErrors.Forbidden;
        }

        var plan = await _store.FindAsync(planId, cancellationToken);
        return plan is null ? PricingErrors.PlanNotFound : RatePlanMapper.ToDetail(plan, await IssuesAsync(plan, cancellationToken));
    }

    /// <summary>
    /// Validation of a plan: the blocking issues and warnings of the plan itself, plus a warning for every channel whose
    /// rule is "own grid" while no plan is published for it.
    /// </summary>
    public async Task<Result<IReadOnlyList<PlanValidationIssue>>> ValidateAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.PricingRead))
        {
            return PricingErrors.Forbidden;
        }

        var plan = await _store.FindAsync(planId, cancellationToken);
        return plan is null ? PricingErrors.PlanNotFound : Result.Success(await IssuesAsync(plan, cancellationToken));
    }

    /// <summary>Creates the draft of an agency and channel (one draft at a time); see <see cref="CreateDraftRequest"/>.</summary>
    public async Task<Result<Guid>> CreateDraftAsync(CreateDraftRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_user.HasPermission(Permissions.PricingManage))
        {
            return PricingErrors.Forbidden;
        }

        var agencies = await _referential.GetAgenciesAsync(cancellationToken);
        if (!agencies.Any(a => a.Id == request.AgencyId))
        {
            return PricingErrors.AgencyNotFound;
        }

        try
        {
            return await CreateDraftInTransactionAsync(request, cancellationToken);
        }
        catch (DbUpdateException) when (!cancellationToken.IsCancellationRequested)
        {
            // Two managers created the draft at the same time: the unique index kept one; report it as a business conflict.
            if (await _store.DraftExistsAsync(request.AgencyId, request.Channel, cancellationToken))
            {
                return PricingErrors.DraftAlreadyExists;
            }

            throw;
        }
    }

    private Task<Result<Guid>> CreateDraftInTransactionAsync(CreateDraftRequest request, CancellationToken cancellationToken) =>
        _unitOfWork.ExecuteAsync(
            async ct =>
            {
                if (await _store.DraftExistsAsync(request.AgencyId, request.Channel, ct))
                {
                    return Result.Failure<Guid>(PricingErrors.DraftAlreadyExists);
                }

                var source = request.SourcePlanId is { } sourceId
                    ? await _store.FindAsync(sourceId, ct)
                    : await _store.FindPublishedAsync(request.AgencyId, request.Channel, ct)
                      ?? (request.Channel is null ? null : await _store.FindPublishedAsync(request.AgencyId, null, ct));
                if (request.SourcePlanId is not null && source?.AgencyId != request.AgencyId)
                {
                    return Result.Failure<Guid>(PricingErrors.PlanNotFound);
                }

                var now = _clock.GetUtcNow();
                var draft = source switch
                {
                    null => RatePlan.CreateDraft(request.AgencyId, request.Channel, request.Name ?? DefaultPlanName, now),
                    _ when source.Channel == request.Channel => source.CopyAsDraft(now, request.Name),
                    _ when source.IsDefaultPlan && request.Channel is { } channel => source.CopyAsChannelDraft(channel, now, request.Name),
                    _ => PricingErrors.PlanNotFound,
                };
                if (draft.IsFailure)
                {
                    return Result.Failure<Guid>(draft.Error);
                }

                _store.Add(draft.Value);
                return draft.Value.Id;
            },
            cancellationToken: cancellationToken);

    public Task<Result<RatePlanDetail>> RenameAsync(Guid planId, int expectedRevision, string name, CancellationToken cancellationToken = default) =>
        EditAsync(planId, expectedRevision, (plan, now) => plan.Rename(name, now), cancellationToken);

    public Task<Result<RatePlanDetail>> UpdateSeasonsAsync(
        Guid planId,
        int expectedRevision,
        IReadOnlyList<SeasonDefinition> seasons,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seasons);
        return EditAsync(planId, expectedRevision, (plan, now) => plan.ReplaceSeasons(seasons, now), cancellationToken);
    }

    public Task<Result<RatePlanDetail>> UpdateEventsAsync(
        Guid planId,
        int expectedRevision,
        IReadOnlyList<PriorityEventDefinition> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        return EditAsync(planId, expectedRevision, (plan, now) => plan.ReplaceEvents(events, now), cancellationToken);
    }

    /// <summary>Replaces the price grid; every category must be one of the plan's agency (ADR-0067).</summary>
    public async Task<Result<RatePlanDetail>> UpdatePricesAsync(
        Guid planId,
        int expectedRevision,
        IReadOnlyList<CategoryPriceDefinition> prices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prices);
        if (!_user.HasPermission(Permissions.PricingManage))
        {
            return PricingErrors.Forbidden;
        }

        if (!await AreCategoriesOfThePlanAsync(planId, prices.Select(p => p.CategoryId), cancellationToken))
        {
            return PricingErrors.CategoryNotFound;
        }

        return await EditAsync(planId, expectedRevision, (plan, now) => plan.ReplacePrices(prices, now), cancellationToken);
    }

    /// <summary>Replaces the duration tiers; every category of a discount must be one of the plan's agency (ADR-0067).</summary>
    public async Task<Result<RatePlanDetail>> UpdateTiersAsync(
        Guid planId,
        int expectedRevision,
        IReadOnlyList<DurationTierDefinition> tiers,
        IReadOnlyList<TierCategoryDiscountDefinition> categoryDiscounts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(categoryDiscounts);
        if (!_user.HasPermission(Permissions.PricingManage))
        {
            return PricingErrors.Forbidden;
        }

        if (!await AreCategoriesOfThePlanAsync(planId, categoryDiscounts.Select(d => d.CategoryId), cancellationToken))
        {
            return PricingErrors.CategoryNotFound;
        }

        return await EditAsync(planId, expectedRevision, (plan, now) => plan.ReplaceTiers(tiers, categoryDiscounts, now), cancellationToken);
    }

    /// <summary>Replaces the extras; a delivery extra must target a location of the plan's agency (otherwise it could never be charged).</summary>
    public async Task<Result<RatePlanDetail>> UpdateExtrasAsync(
        Guid planId,
        int expectedRevision,
        IReadOnlyList<ExtraDefinition> extras,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extras);
        if (!_user.HasPermission(Permissions.PricingManage))
        {
            return PricingErrors.Forbidden;
        }

        if (extras.Any(e => e.Kind == ExtraKind.LocationDelivery) && await _store.FindAgencyIdAsync(planId, cancellationToken) is { } agencyId)
        {
            var locations = (await _referential.GetLocationsAsync(agencyId, cancellationToken)).Select(l => l.Id).ToHashSet();
            if (extras.Any(e => e.Kind == ExtraKind.LocationDelivery && (e.LocationId is not { } locationId || !locations.Contains(locationId))))
            {
                return PricingErrors.ExtraInvalid;
            }
        }

        return await EditAsync(planId, expectedRevision, (plan, now) => plan.ReplaceExtras(extras, now), cancellationToken);
    }

    public Task<Result<RatePlanDetail>> UpdatePromotionsAsync(
        Guid planId,
        int expectedRevision,
        IReadOnlyList<PromotionDefinition> promotions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(promotions);
        return EditAsync(planId, expectedRevision, (plan, now) => plan.ReplacePromotions(promotions, now), cancellationToken);
    }

    public Task<Result<RatePlanDetail>> UpdateChannelRulesAsync(
        Guid planId,
        int expectedRevision,
        IReadOnlyList<ChannelRuleDefinition> rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return EditAsync(planId, expectedRevision, (plan, now) => plan.ReplaceChannelRules(rules, now), cancellationToken);
    }

    /// <summary>Publishes a draft as the next version of its agency and channel; the plan it replaces is archived.</summary>
    public async Task<Result<RatePlanDetail>> PublishAsync(Guid planId, int expectedRevision, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.PricingManage))
        {
            return PricingErrors.Forbidden;
        }

        return await SaveAsync(
            async ct =>
            {
                var plan = await LoadDraftAsync(planId, expectedRevision, ct);
                if (plan.IsFailure)
                {
                    return Result.Failure<RatePlanDetail>(plan.Error);
                }

                var draft = plan.Value;
                var current = await _store.FindPublishedAsync(draft.AgencyId, draft.Channel, ct);
                var lastVersion = await _store.GetLastVersionAsync(draft.AgencyId, draft.Channel, ct);
                var published = draft.Publish(current, lastVersion, _clock.GetUtcNow());
                return published.IsFailure
                    ? Result.Failure<RatePlanDetail>(published.Error)
                    : RatePlanMapper.ToDetail(draft, await IssuesAsync(draft, ct));
            },
            cancellationToken);
    }

    /// <summary>Archives a draft (abandoned) or a published plan (its agency and channel are then no longer priced).</summary>
    public async Task<Result<RatePlanDetail>> ArchiveAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.PricingManage))
        {
            return PricingErrors.Forbidden;
        }

        return await SaveAsync(
            async ct =>
            {
                var plan = await _store.FindAsync(planId, ct);
                if (plan is null)
                {
                    return Result.Failure<RatePlanDetail>(PricingErrors.PlanNotFound);
                }

                var archived = plan.Archive(_clock.GetUtcNow());
                return archived.IsFailure ? Result.Failure<RatePlanDetail>(archived.Error) : RatePlanMapper.ToDetail(plan, []);
            },
            cancellationToken);
    }

    /// <summary>Prices a request with the published plans and, when asked, with another plan of the agency in their place.</summary>
    public async Task<Result<SimulationResult>> SimulateAsync(SimulationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Request);
        if (!_user.HasPermission(Permissions.PricingRead))
        {
            return PricingErrors.Forbidden;
        }

        var quotedAt = request.QuotedAt ?? _clock.GetUtcNow();
        var published = await _calculator.QuoteAsync(request.Request, quotedAt, replacement: null, cancellationToken);
        if (request.ComparedPlanId is not { } comparedId)
        {
            return new SimulationResult(QuoteOutcome.From(published), null);
        }

        var compared = await _store.FindAsync(comparedId, cancellationToken);
        Result<PriceQuote> comparison = compared switch
        {
            null => PricingErrors.PlanNotFound,
            _ when compared.AgencyId != request.Request.AgencyId => PricingErrors.PlanNotFound,
            _ when !compared.IsDefaultPlan && compared.Channel != request.Request.Channel => PricingErrors.SimulationChannelMismatch,
            _ => await _calculator.QuoteAsync(request.Request, quotedAt, compared, cancellationToken),
        };
        return new SimulationResult(QuoteOutcome.From(published), QuoteOutcome.From(comparison));
    }

    private async Task<Result<RatePlanDetail>> EditAsync(
        Guid planId,
        int expectedRevision,
        Func<RatePlan, DateTimeOffset, Result> edit,
        CancellationToken cancellationToken)
    {
        if (!_user.HasPermission(Permissions.PricingManage))
        {
            return PricingErrors.Forbidden;
        }

        return await SaveAsync(
            async ct =>
            {
                var plan = await LoadDraftAsync(planId, expectedRevision, ct);
                if (plan.IsFailure)
                {
                    return Result.Failure<RatePlanDetail>(plan.Error);
                }

                var edited = edit(plan.Value, _clock.GetUtcNow());
                return edited.IsFailure
                    ? Result.Failure<RatePlanDetail>(edited.Error)
                    : RatePlanMapper.ToDetail(plan.Value, await IssuesAsync(plan.Value, ct));
            },
            cancellationToken);
    }

    /// <summary>
    /// Whether every category is one of the plan's agency, inactive ones included (ADR-0067): a price or a discount for a
    /// category of another agency would never apply. A plan the operation does not see is reported by the edit itself.
    /// </summary>
    private async Task<bool> AreCategoriesOfThePlanAsync(Guid planId, IEnumerable<Guid> categoryIds, CancellationToken cancellationToken)
    {
        var requested = categoryIds.ToHashSet();
        if (requested.Count == 0 || await _store.FindAgencyIdAsync(planId, cancellationToken) is not { } agencyId)
        {
            return true;
        }

        var categories = await _fleet.GetCategoriesAsync(agencyId, includeInactive: true, cancellationToken);
        return requested.IsSubsetOf(categories.Select(c => c.Id));
    }

    /// <summary>The tracked draft, if it still has the revision the editor read.</summary>
    private async Task<Result<RatePlan>> LoadDraftAsync(Guid planId, int expectedRevision, CancellationToken cancellationToken)
    {
        var plan = await _store.FindAsync(planId, cancellationToken);
        if (plan is null)
        {
            return PricingErrors.PlanNotFound;
        }

        if (!plan.IsDraft)
        {
            return PricingErrors.PlanNotEditable;
        }

        return plan.Revision == expectedRevision ? plan : PricingErrors.PlanModifiedConcurrently;
    }

    /// <summary>Runs a change in a unit of work; a concurrent save of the same plan is reported, not thrown.</summary>
    private async Task<Result<RatePlanDetail>> SaveAsync(
        Func<CancellationToken, Task<Result<RatePlanDetail>>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _unitOfWork.ExecuteAsync(operation, cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return PricingErrors.PlanModifiedConcurrently;
        }
    }

    private async Task<IReadOnlyList<PlanValidationIssue>> IssuesAsync(RatePlan plan, CancellationToken cancellationToken)
    {
        var issues = plan.Validate().ToList();
        foreach (var rule in plan.ChannelRules.Where(r => r.Mode == ChannelPricingMode.OwnGrid))
        {
            if (await _store.FindPublishedAsync(plan.AgencyId, rule.Channel, cancellationToken) is null)
            {
                issues.Add(new PlanValidationIssue(PlanValidationCodes.ChannelPlanMissing, IsBlocking: false, [rule.Channel.ToString()]));
            }
        }

        return issues;
    }
}
