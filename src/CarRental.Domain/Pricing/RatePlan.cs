using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>
/// A versioned rate plan (grille tarifaire) of an agency: the default plan (<see cref="Channel"/> null), which also holds
/// the channel rules, or the own grid of one sales channel. Content: dated seasons, priority events, daily prices per
/// category and period (TTC), duration tiers, extras, promotions and channel rules.
/// </summary>
/// <remarks>
/// <para>Only a <see cref="RatePlanStatus.Draft"/> is edited; publishing gives it the next version number of its agency and
/// channel and archives the plan it replaces, so a published plan never changes and every quote can name the exact
/// version it was computed with (ADR-0023).</para>
/// <para>Every edit replaces one section as a whole, checks the invariants of that section, increments
/// <see cref="Revision"/> (optimistic check of the editor) and raises <see cref="RatePlanDraftUpdated"/>.</para>
/// </remarks>
public sealed class RatePlan : AggregateRoot, IAgencyOwned
{
    public const int NameMaxLength = 120;

    private readonly List<Season> _seasons = [];
    private readonly List<PriorityEvent> _events = [];
    private readonly List<CategoryPrice> _prices = [];
    private readonly List<DurationTier> _tiers = [];
    private readonly List<TierCategoryDiscount> _tierCategoryDiscounts = [];
    private readonly List<Extra> _extras = [];
    private readonly List<Promotion> _promotions = [];
    private readonly List<ChannelRule> _channelRules = [];

    private RatePlan()
    {
    }

    private RatePlan(Guid id, Guid agencyId, SalesChannel? channel, string name, Guid? basedOnPlanId, DateTimeOffset now)
        : base(id)
    {
        AgencyId = agencyId;
        Channel = channel;
        Name = name;
        BasedOnPlanId = basedOnPlanId;
        Status = RatePlanStatus.Draft;
        CreatedAtUtc = now.ToUniversalTime();
        ModifiedAtUtc = CreatedAtUtc;
    }

    /// <summary>Agency (Referential module) the plan belongs to.</summary>
    public Guid AgencyId { get; private set; }

    /// <summary>The channel of an own-grid plan; <see langword="null"/> for the default plan of the agency.</summary>
    public SalesChannel? Channel { get; private set; }

    public string Name { get; private set; } = null!;

    public RatePlanStatus Status { get; private set; }

    /// <summary>Version number given at publication (1, 2, 3… per agency and channel); <see langword="null"/> for a draft.</summary>
    public int? Version { get; private set; }

    /// <summary>The plan this draft was copied from.</summary>
    public Guid? BasedOnPlanId { get; private set; }

    /// <summary>Incremented by every edit; the editor sends the revision it read to detect concurrent changes.</summary>
    public int Revision { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ModifiedAtUtc { get; private set; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public DateTimeOffset? ArchivedAtUtc { get; private set; }

    public bool IsDefaultPlan => Channel is null;

    public bool IsDraft => Status == RatePlanStatus.Draft;

    /// <summary>Seasons ordered by start date.</summary>
    public IReadOnlyList<Season> Seasons => [.. _seasons.OrderBy(s => s.StartDate)];

    /// <summary>Priority events ordered by start date, then priority.</summary>
    public IReadOnlyList<PriorityEvent> Events =>
        [.. _events.OrderBy(e => e.StartDate).ThenByDescending(e => e.Priority).ThenBy(e => e.Name, StringComparer.Ordinal)];

    /// <summary>Cells of the price grid, ordered by category then period.</summary>
    public IReadOnlyList<CategoryPrice> Prices =>
        [.. _prices.OrderBy(p => p.CategoryId).ThenBy(p => p.PeriodKind).ThenBy(p => p.EventId)];

    /// <summary>Duration tiers ordered by their first day.</summary>
    public IReadOnlyList<DurationTier> Tiers => [.. _tiers.OrderBy(t => t.MinimumDays)];

    public IReadOnlyList<TierCategoryDiscount> TierCategoryDiscounts =>
        [.. _tierCategoryDiscounts.OrderBy(d => d.TierMinimumDays).ThenBy(d => d.CategoryId)];

    /// <summary>Extras in catalogue order.</summary>
    public IReadOnlyList<Extra> Extras => [.. _extras.OrderBy(e => e.Position)];

    /// <summary>Promotions in catalogue order.</summary>
    public IReadOnlyList<Promotion> Promotions => [.. _promotions.OrderBy(p => p.Position)];

    public IReadOnlyList<ChannelRule> ChannelRules => [.. _channelRules.OrderBy(r => r.Channel)];

    /// <summary>Categories that have a base price (hence a price on every date).</summary>
    public IReadOnlyList<Guid> PricedCategoryIds =>
        [.. _prices.Where(p => p.PeriodKind == RatePeriodKind.Base).Select(p => p.CategoryId).Distinct().Order()];

    /// <summary>Starts an empty draft (first plan of an agency or of a channel).</summary>
    public static Result<RatePlan> CreateDraft(Guid agencyId, SalesChannel? channel, string name, DateTimeOffset now)
    {
        if (agencyId == Guid.Empty)
        {
            return PricingErrors.AgencyNotFound;
        }

        if (channel is { } value && !Enum.IsDefined(value))
        {
            return PricingErrors.ChannelRuleInvalid;
        }

        if (NormalizeName(name) is not { } normalized)
        {
            return PricingErrors.PlanNameInvalid;
        }

        var plan = new RatePlan(NewId(), agencyId, channel, normalized, basedOnPlanId: null, now);
        plan.Raise(new RatePlanCreated(plan.Id, agencyId, channel, BasedOnPlanId: null));
        return plan;
    }

    /// <summary>
    /// A new draft with the content of this plan (any status), for the same agency and channel. Children get new ids.
    /// </summary>
    public Result<RatePlan> CopyAsDraft(DateTimeOffset now, string? name = null) => CopyCore(Channel, name, now);

    /// <summary>
    /// A first draft of an own grid for <paramref name="channel"/>, copied from this default plan (channel rules excluded).
    /// </summary>
    public Result<RatePlan> CopyAsChannelDraft(SalesChannel channel, DateTimeOffset now, string? name = null)
    {
        if (!IsDefaultPlan || !Enum.IsDefined(channel))
        {
            return PricingErrors.ChannelRuleInvalid;
        }

        return CopyCore(channel, name, now);
    }

    public Result Rename(string name, DateTimeOffset now)
    {
        if (!IsDraft)
        {
            return PricingErrors.PlanNotEditable;
        }

        if (NormalizeName(name) is not { } normalized)
        {
            return PricingErrors.PlanNameInvalid;
        }

        Name = normalized;
        Touch(RatePlanSection.Name, now);
        return Result.Success();
    }

    /// <summary>Replaces the seasons. Seasons never overlap; prices are entered per season level and are kept.</summary>
    public Result ReplaceSeasons(IEnumerable<SeasonDefinition> seasons, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(seasons);
        if (!IsDraft)
        {
            return PricingErrors.PlanNotEditable;
        }

        var definitions = seasons.ToList();
        if (definitions.Exists(d => d is null || !Season.IsValid(d)))
        {
            return PricingErrors.SeasonInvalid;
        }

        var ordered = definitions.OrderBy(d => d.StartDate).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].StartDate <= ordered[i - 1].EndDate)
            {
                return PricingErrors.SeasonsOverlap;
            }
        }

        MergeById(_seasons, definitions, d => d.Id, d => Season.Create(this, d), (season, definition) => season.Apply(definition));
        Touch(RatePlanSection.Seasons, now);
        return Result.Success();
    }

    /// <summary>
    /// Replaces the priority events. Overlapping events need different priorities. Events keep their id (and prices) when
    /// the definition carries it; the prices of removed events are removed.
    /// </summary>
    public Result ReplaceEvents(IEnumerable<PriorityEventDefinition> events, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (!IsDraft)
        {
            return PricingErrors.PlanNotEditable;
        }

        var definitions = events.ToList();
        if (definitions.Exists(d => d is null || !PriorityEvent.IsValid(d)))
        {
            return PricingErrors.EventInvalid;
        }

        for (var i = 0; i < definitions.Count; i++)
        {
            for (var j = i + 1; j < definitions.Count; j++)
            {
                var (a, b) = (definitions[i], definitions[j]);
                if (a.Priority == b.Priority && a.StartDate <= b.EndDate && b.StartDate <= a.EndDate)
                {
                    return PricingErrors.EventPriorityConflict;
                }
            }
        }

        MergeById(_events, definitions, d => d.Id, d => PriorityEvent.Create(this, d), (priorityEvent, definition) => priorityEvent.Apply(definition));
        var eventIds = _events.Select(e => e.Id).ToHashSet();
        _prices.RemoveAll(p => p.PeriodKind == RatePeriodKind.Event && !eventIds.Contains(p.EventId!.Value));
        Touch(RatePlanSection.Events, now);
        return Result.Success();
    }

    /// <summary>
    /// Replaces the price grid. Prices are positive TTC amounts; each priced category needs a base price; an event price
    /// refers to an event of the plan; a category has one price per period.
    /// </summary>
    public Result ReplacePrices(IEnumerable<CategoryPriceDefinition> prices, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(prices);
        if (!IsDraft)
        {
            return PricingErrors.PlanNotEditable;
        }

        var definitions = prices.ToList();
        var eventIds = _events.Select(e => e.Id).ToHashSet();
        if (definitions.Exists(d => d is null
                                    || d.CategoryId == Guid.Empty
                                    || !PricingValues.IsPositiveTnd(d.DailyPrice)
                                    || RatePeriodKey.From(d.Period.Kind, d.Period.EventId) is null
                                    || (d.Period.Kind == RatePeriodKind.Event && !eventIds.Contains(d.Period.EventId!.Value))))
        {
            return PricingErrors.PriceInvalid;
        }

        if (definitions.GroupBy(d => (d.CategoryId, d.Period)).Any(g => g.Count() > 1))
        {
            return PricingErrors.PriceDuplicate;
        }

        if (definitions.GroupBy(d => d.CategoryId).Any(g => !g.Any(d => d.Period.IsBase)))
        {
            return PricingErrors.BasePriceMissing;
        }

        var existing = _prices.ToDictionary(p => (p.CategoryId, p.Period));
        var kept = new HashSet<CategoryPrice>();
        foreach (var definition in definitions)
        {
            if (existing.TryGetValue((definition.CategoryId, definition.Period), out var price))
            {
                price.ChangePrice(definition.DailyPrice);
                kept.Add(price);
            }
            else
            {
                var created = CategoryPrice.Create(this, definition);
                _prices.Add(created);
                kept.Add(created);
            }
        }

        _prices.RemoveAll(p => !kept.Contains(p));
        Touch(RatePlanSection.Prices, now);
        return Result.Success();
    }

    /// <summary>
    /// Replaces the duration tiers and their per-category discounts. Tiers start at day 1 with strictly increasing first
    /// days (contiguous by construction, the last one open-ended); discounts are between 0 and 99.99 %.
    /// </summary>
    public Result ReplaceTiers(
        IEnumerable<DurationTierDefinition> tiers,
        IEnumerable<TierCategoryDiscountDefinition> categoryDiscounts,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(categoryDiscounts);
        if (!IsDraft)
        {
            return PricingErrors.PlanNotEditable;
        }

        var tierDefinitions = tiers.ToList();
        if (tierDefinitions.Exists(d => d is null))
        {
            return PricingErrors.TierInvalid;
        }

        tierDefinitions = [.. tierDefinitions.OrderBy(d => d.MinimumDays)];
        var tiersValid = tierDefinitions.Count == 0 || tierDefinitions[0].MinimumDays == 1;
        for (var i = 0; tiersValid && i < tierDefinitions.Count; i++)
        {
            var tier = tierDefinitions[i];
            tiersValid = tier.MinimumDays is >= 1 and <= PricingValues.MaximumMinimumDays
                         && PricingValues.IsPercentage(tier.DiscountPercent, allowZero: true, allowHundred: false)
                         && (i == 0 || tier.MinimumDays > tierDefinitions[i - 1].MinimumDays);
        }

        if (!tiersValid)
        {
            return PricingErrors.TierInvalid;
        }

        var tierStarts = tierDefinitions.Select(t => t.MinimumDays).ToHashSet();
        var discountDefinitions = categoryDiscounts.ToList();
        if (discountDefinitions.Exists(d => d is null
                                            || !tierStarts.Contains(d.TierMinimumDays)
                                            || d.CategoryId == Guid.Empty
                                            || !PricingValues.IsPercentage(d.DiscountPercent, allowZero: true, allowHundred: false))
            || discountDefinitions.GroupBy(d => (d.TierMinimumDays, d.CategoryId)).Any(g => g.Count() > 1))
        {
            return PricingErrors.TierOverrideInvalid;
        }

        var existingTiers = _tiers.ToDictionary(t => t.MinimumDays);
        _tiers.Clear();
        foreach (var definition in tierDefinitions)
        {
            if (existingTiers.TryGetValue(definition.MinimumDays, out var tier))
            {
                tier.ChangeDiscount(definition.DiscountPercent);
                _tiers.Add(tier);
            }
            else
            {
                _tiers.Add(DurationTier.Create(this, definition));
            }
        }

        var existingDiscounts = _tierCategoryDiscounts.ToDictionary(d => (d.TierMinimumDays, d.CategoryId));
        _tierCategoryDiscounts.Clear();
        foreach (var definition in discountDefinitions)
        {
            if (existingDiscounts.TryGetValue((definition.TierMinimumDays, definition.CategoryId), out var discount))
            {
                discount.ChangeDiscount(definition.DiscountPercent);
                _tierCategoryDiscounts.Add(discount);
            }
            else
            {
                _tierCategoryDiscounts.Add(TierCategoryDiscount.Create(this, definition));
            }
        }

        Touch(RatePlanSection.Tiers, now);
        return Result.Success();
    }

    /// <summary>
    /// Replaces the extras catalogue, in the given order. Codes are unique; only one active extra of each automatic kind
    /// (young driver, after-hours, one-way, franchise waiver) and one active delivery per location.
    /// </summary>
    public Result ReplaceExtras(IEnumerable<ExtraDefinition> extras, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(extras);
        if (!IsDraft)
        {
            return PricingErrors.PlanNotEditable;
        }

        var definitions = extras.ToList();
        if (definitions.Exists(d => d is null || !Extra.IsValid(d)))
        {
            return PricingErrors.ExtraInvalid;
        }

        if (definitions.GroupBy(d => Extra.NormalizeCode(d.Code), StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            return PricingErrors.ExtraCodeDuplicate;
        }

        var active = definitions.Where(d => d.IsActive).ToList();
        var duplicateKind = active
            .Where(d => d.Kind is ExtraKind.YoungDriver or ExtraKind.AfterHours or ExtraKind.OneWay or ExtraKind.FranchiseWaiver)
            .GroupBy(d => d.Kind)
            .Any(g => g.Count() > 1);
        var duplicateDelivery = active
            .Where(d => d.Kind == ExtraKind.LocationDelivery)
            .GroupBy(d => d.LocationId)
            .Any(g => g.Count() > 1);
        if (duplicateKind || duplicateDelivery)
        {
            return PricingErrors.ExtraKindDuplicate;
        }

        var byId = _extras.ToDictionary(e => e.Id);
        var byCode = _extras.ToDictionary(e => e.Code, StringComparer.Ordinal);
        var result = new List<Extra>(definitions.Count);
        for (var position = 0; position < definitions.Count; position++)
        {
            var definition = definitions[position];
            var existing = definition.Id is { } id && byId.TryGetValue(id, out var withId) ? withId
                : byCode.GetValueOrDefault(Extra.NormalizeCode(definition.Code));
            if (existing is not null && !result.Contains(existing))
            {
                existing.Apply(definition, position);
                result.Add(existing);
            }
            else
            {
                result.Add(Extra.Create(this, definition, position));
            }
        }

        _extras.Clear();
        _extras.AddRange(result);
        Touch(RatePlanSection.Extras, now);
        return Result.Success();
    }

    /// <summary>Replaces the promotions, in the given order. Promotion codes are unique (case-insensitive).</summary>
    public Result ReplacePromotions(IEnumerable<PromotionDefinition> promotions, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(promotions);
        if (!IsDraft)
        {
            return PricingErrors.PlanNotEditable;
        }

        var definitions = promotions.ToList();
        if (definitions.Exists(d => d is null || !Promotion.IsValid(d)))
        {
            return PricingErrors.PromotionInvalid;
        }

        if (definitions.Where(d => d.Kind == PromotionKind.Code)
            .GroupBy(d => Promotion.NormalizeCode(d.Code), StringComparer.Ordinal)
            .Any(g => g.Count() > 1))
        {
            return PricingErrors.PromotionCodeDuplicate;
        }

        var byId = _promotions.ToDictionary(p => p.Id);
        var result = new List<Promotion>(definitions.Count);
        for (var position = 0; position < definitions.Count; position++)
        {
            var definition = definitions[position];
            if (definition.Id is { } id && byId.TryGetValue(id, out var existing) && !result.Contains(existing))
            {
                existing.Apply(definition, position);
                result.Add(existing);
            }
            else
            {
                result.Add(Promotion.Create(this, definition, position));
            }
        }

        _promotions.Clear();
        _promotions.AddRange(result);
        Touch(RatePlanSection.Promotions, now);
        return Result.Success();
    }

    /// <summary>Replaces the channel rules (default plan only): one rule per channel; a channel without rule is at parity.</summary>
    public Result ReplaceChannelRules(IEnumerable<ChannelRuleDefinition> rules, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!IsDraft)
        {
            return PricingErrors.PlanNotEditable;
        }

        var definitions = rules.ToList();
        if (!IsDefaultPlan && definitions.Count > 0)
        {
            return PricingErrors.ChannelRulesOnChannelPlan;
        }

        if (definitions.Exists(d => d is null || !ChannelRule.IsValid(d)) || definitions.GroupBy(d => d.Channel).Any(g => g.Count() > 1))
        {
            return PricingErrors.ChannelRuleInvalid;
        }

        var existing = _channelRules.ToDictionary(r => r.Channel);
        _channelRules.Clear();
        foreach (var definition in definitions)
        {
            if (existing.TryGetValue(definition.Channel, out var rule))
            {
                rule.Apply(definition);
                _channelRules.Add(rule);
            }
            else
            {
                _channelRules.Add(ChannelRule.Create(this, definition));
            }
        }

        Touch(RatePlanSection.ChannelRules, now);
        return Result.Success();
    }

    /// <summary>
    /// Completeness checks before publication: blocking issues prevent it, warnings point at probable mistakes
    /// (dates between seasons priced at the base price, events without any price).
    /// </summary>
    public IReadOnlyList<PlanValidationIssue> Validate()
    {
        var issues = new List<PlanValidationIssue>();
        if (PricedCategoryIds.Count == 0)
        {
            issues.Add(new PlanValidationIssue(PlanValidationCodes.NoPrice, IsBlocking: true, []));
        }

        var seasons = Seasons;
        if (seasons.Count == 0)
        {
            issues.Add(new PlanValidationIssue(PlanValidationCodes.NoSeason, IsBlocking: false, []));
        }

        for (var i = 1; i < seasons.Count; i++)
        {
            var gapStart = seasons[i - 1].EndDate.AddDays(1);
            var gapEnd = seasons[i].StartDate.AddDays(-1);
            if (gapStart <= gapEnd)
            {
                issues.Add(new PlanValidationIssue(PlanValidationCodes.SeasonGap, IsBlocking: false, [Iso(gapStart), Iso(gapEnd)]));
            }
        }

        foreach (var priorityEvent in Events.Where(e => !_prices.Exists(p => p.EventId == e.Id)))
        {
            issues.Add(new PlanValidationIssue(PlanValidationCodes.EventWithoutPrice, IsBlocking: false, [priorityEvent.Name]));
        }

        return issues;
    }

    /// <summary>
    /// Publishes this draft as the next version of its agency and channel: <paramref name="currentlyPublished"/> (the plan
    /// in force, if any) is archived and <see cref="Version"/> becomes <paramref name="lastVersion"/> + 1, where
    /// <paramref name="lastVersion"/> is the highest version ever published for the agency and channel (0 for none).
    /// </summary>
    public Result Publish(RatePlan? currentlyPublished, int lastVersion, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastVersion);
        if (!IsDraft)
        {
            return PricingErrors.PlanNotEditable;
        }

        if (Validate().Any(i => i.IsBlocking))
        {
            return PricingErrors.PlanInvalid;
        }

        if (currentlyPublished is not null)
        {
            if (currentlyPublished.AgencyId != AgencyId
                || currentlyPublished.Channel != Channel
                || currentlyPublished.Status != RatePlanStatus.Published
                || ReferenceEquals(currentlyPublished, this))
            {
                throw new InvalidOperationException("The plan to replace must be the published plan of the same agency and channel.");
            }

            if (currentlyPublished.Version > lastVersion)
            {
                throw new InvalidOperationException("The last version cannot be lower than the version of the published plan.");
            }

            currentlyPublished.ArchiveCore(now);
        }

        Version = lastVersion + 1;
        Status = RatePlanStatus.Published;
        PublishedAtUtc = now.ToUniversalTime();
        ModifiedAtUtc = PublishedAtUtc.Value;
        Raise(new RatePlanPublished(Id, AgencyId, Channel, Version.Value, currentlyPublished?.Id));
        return Result.Success();
    }

    /// <summary>Withdraws the plan: a draft is abandoned, a published plan stops pricing its agency and channel.</summary>
    public Result Archive(DateTimeOffset now)
    {
        if (Status == RatePlanStatus.Archived)
        {
            return PricingErrors.PlanAlreadyArchived;
        }

        ArchiveCore(now);
        return Result.Success();
    }

    /// <summary>Daily price TTC of a category for a grid column, if entered.</summary>
    public Money? FindPrice(Guid categoryId, RatePeriodKey period) =>
        _prices.Find(p => p.CategoryId == categoryId && p.Period == period)?.DailyPrice;

    /// <summary>The season covering <paramref name="date"/> (seasons never overlap).</summary>
    public Season? FindSeason(DateOnly date) => _seasons.Find(s => s.Covers(date));

    /// <summary>Events covering <paramref name="date"/>, highest priority first.</summary>
    public IReadOnlyList<PriorityEvent> FindEvents(DateOnly date) =>
        [.. _events.Where(e => e.Covers(date)).OrderByDescending(e => e.Priority)];

    public PriorityEvent? FindEvent(Guid eventId) => _events.Find(e => e.Id == eventId);

    /// <summary>The tier of a rental of <paramref name="days"/> days (the last tier starting on or before it).</summary>
    public DurationTier? FindTier(int days) => _tiers.Where(t => t.MinimumDays <= days).MaxBy(t => t.MinimumDays);

    /// <summary>The first day of the tier following <paramref name="tier"/>, or <see langword="null"/> for the last tier.</summary>
    public int? NextTierStart(DurationTier tier)
    {
        ArgumentNullException.ThrowIfNull(tier);
        return _tiers.Where(t => t.MinimumDays > tier.MinimumDays).Select(t => (int?)t.MinimumDays).Min();
    }

    /// <summary>Discount percentage of <paramref name="tier"/> for a category (its override, else the plan percentage).</summary>
    public decimal TierDiscountPercent(DurationTier tier, Guid categoryId)
    {
        ArgumentNullException.ThrowIfNull(tier);
        return _tierCategoryDiscounts.Find(d => d.TierMinimumDays == tier.MinimumDays && d.CategoryId == categoryId)?.DiscountPercent
            ?? tier.DiscountPercent;
    }

    /// <summary>Rule of a channel (default plan); <see langword="null"/> means parity.</summary>
    public ChannelRule? FindChannelRule(SalesChannel channel) => _channelRules.Find(r => r.Channel == channel);

    private static string? NormalizeName(string? name) =>
        name?.Trim() is { Length: > 0 and <= NameMaxLength } trimmed ? trimmed : null;

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Updates the children whose id is in the definitions, creates the others and removes the rest.</summary>
    private static void MergeById<TChild, TDefinition>(
        List<TChild> children,
        IReadOnlyList<TDefinition> definitions,
        Func<TDefinition, Guid?> idOf,
        Func<TDefinition, TChild> create,
        Action<TChild, TDefinition> update)
        where TChild : Entity
    {
        var existing = children.ToDictionary(c => c.Id);
        var result = new List<TChild>(definitions.Count);
        foreach (var definition in definitions)
        {
            if (idOf(definition) is { } id && existing.Remove(id, out var child))
            {
                update(child, definition);
                result.Add(child);
            }
            else
            {
                result.Add(create(definition));
            }
        }

        children.Clear();
        children.AddRange(result);
    }

    private Result<RatePlan> CopyCore(SalesChannel? channel, string? name, DateTimeOffset now)
    {
        if (NormalizeName(name ?? Name) is not { } normalized)
        {
            return PricingErrors.PlanNameInvalid;
        }

        var copy = new RatePlan(NewId(), AgencyId, channel, normalized, Id, now);
        copy._seasons.AddRange(_seasons.Select(s => s.CopyForNewPlan(copy)));

        var eventIds = new Dictionary<Guid, Guid>();
        foreach (var priorityEvent in _events)
        {
            var copied = priorityEvent.CopyForNewPlan(copy);
            eventIds[priorityEvent.Id] = copied.Id;
            copy._events.Add(copied);
        }

        foreach (var price in _prices)
        {
            var period = price.Period.EventId is { } eventId ? RatePeriodKey.ForEvent(eventIds[eventId]) : price.Period;
            copy._prices.Add(CategoryPrice.Create(copy, new CategoryPriceDefinition(price.CategoryId, period, price.DailyPrice)));
        }

        copy._tiers.AddRange(_tiers.Select(t => t.CopyForNewPlan(copy)));
        copy._tierCategoryDiscounts.AddRange(_tierCategoryDiscounts.Select(d => d.CopyForNewPlan(copy)));
        copy._extras.AddRange(_extras.Select(e => e.CopyForNewPlan(copy)));
        copy._promotions.AddRange(_promotions.Select(p => p.CopyForNewPlan(copy)));
        if (channel is null)
        {
            copy._channelRules.AddRange(_channelRules.Select(r => r.CopyForNewPlan(copy)));
        }

        copy.Raise(new RatePlanCreated(copy.Id, AgencyId, channel, Id));
        return copy;
    }

    private void ArchiveCore(DateTimeOffset now)
    {
        Status = RatePlanStatus.Archived;
        ArchivedAtUtc = now.ToUniversalTime();
        ModifiedAtUtc = ArchivedAtUtc.Value;
        Raise(new RatePlanArchived(Id, AgencyId, Channel, Version));
    }

    private void Touch(RatePlanSection section, DateTimeOffset now)
    {
        Revision++;
        ModifiedAtUtc = now.ToUniversalTime();
        Raise(new RatePlanDraftUpdated(Id, section, Revision));
    }
}
