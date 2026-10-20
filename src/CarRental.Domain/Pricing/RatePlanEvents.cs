using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>A draft rate plan was created (empty, or copied from <paramref name="BasedOnPlanId"/>).</summary>
public sealed record RatePlanCreated(Guid RatePlanId, Guid AgencyId, SalesChannel? Channel, Guid? BasedOnPlanId) : IDomainEvent;

/// <summary>A section of a draft rate plan was replaced; <paramref name="Revision"/> is the new revision of the plan.</summary>
public sealed record RatePlanDraftUpdated(Guid RatePlanId, RatePlanSection Section, int Revision) : IDomainEvent;

/// <summary>
/// A rate plan became the plan in force of its agency and channel, replacing <paramref name="ReplacedPlanId"/>
/// (archived). Channels (phase 5) push the new rates from this event.
/// </summary>
public sealed record RatePlanPublished(Guid RatePlanId, Guid AgencyId, SalesChannel? Channel, int Version, Guid? ReplacedPlanId) : IDomainEvent;

/// <summary>A rate plan was archived (draft abandoned, or published plan replaced or withdrawn).</summary>
public sealed record RatePlanArchived(Guid RatePlanId, Guid AgencyId, SalesChannel? Channel, int? Version) : IDomainEvent;
