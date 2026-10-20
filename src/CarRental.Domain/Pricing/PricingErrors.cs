using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>Business errors of the Pricing module (codes <c>pricing.*</c>; texts in <c>Error.{code}</c> resources).</summary>
public static class PricingErrors
{
    public const string MinimumDurationNotMetCode = "pricing.minimum_duration_not_met";

    // Quote (engine and pricing service)
    public static readonly Error PeriodInvalid = Error.Validation(
        "pricing.period_invalid",
        "The return must be strictly after the pick-up.");

    public static readonly Error RentalTooLong = Error.Validation(
        "pricing.rental_too_long",
        $"A rental cannot exceed {PricingEngine.MaximumRentalDays} days.");

    public static readonly Error NoPublishedPlan = Error.NotFound(
        "pricing.no_published_plan",
        "The agency has no published rate plan.");

    public static readonly Error ChannelPlanMissing = Error.NotFound(
        "pricing.channel_plan_missing",
        "The channel is priced with its own grid but no plan is published for it.");

    public static readonly Error CategoryNotFound = Error.NotFound(
        "pricing.category_not_found",
        "The vehicle category does not exist or is inactive.");

    public static readonly Error CategoryNotPriced = Error.NotFound(
        "pricing.category_not_priced",
        "The rate plan has no price for this vehicle category on the requested dates.");

    public static readonly Error LocationNotFound = Error.NotFound(
        "pricing.location_not_found",
        "The pick-up or return location is not an active location of the agency.");

    public static readonly Error VehicleNotFound = Error.NotFound(
        "pricing.vehicle_not_found",
        "The guaranteed vehicle does not exist or is sold.");

    public static readonly Error GuaranteedModelNotOffered = Error.Validation(
        "pricing.guaranteed_model_not_offered",
        "The vehicle has no guaranteed-model supplement: it cannot be booked as a guaranteed model.");

    public static readonly Error GuaranteedVehicleCategoryMismatch = Error.Validation(
        "pricing.guaranteed_vehicle_category_mismatch",
        "The guaranteed vehicle does not belong to the requested category.");

    public static readonly Error ExtraUnknown = Error.Validation(
        "pricing.extra_unknown",
        "An extra of the request is not an active extra of the rate plan.");

    public static readonly Error ExtraNotSelectable = Error.Validation(
        "pricing.extra_not_selectable",
        "An extra of the request is added automatically and cannot be selected.");

    public static readonly Error ExtraQuantityInvalid = Error.Validation(
        "pricing.extra_quantity_invalid",
        $"Extra quantities are between 1 and {PricingEngine.MaximumExtraQuantity} (1 for the franchise waiver).");

    public static readonly Error DriversInvalid = Error.Validation(
        "pricing.drivers_invalid",
        $"At most {PricingEngine.MaximumDrivers} drivers, each born before the pick-up date.");

    // Administration of rate plans
    public static readonly Error Forbidden = Error.Forbidden(
        "pricing.forbidden",
        "The current user lacks the required pricing permission.");

    public static readonly Error AgencyNotFound = Error.NotFound("pricing.agency_not_found", "The agency does not exist.");

    public static readonly Error PlanNotFound = Error.NotFound("pricing.plan_not_found", "The rate plan does not exist.");

    public static readonly Error PlanNotEditable = Error.Conflict(
        "pricing.plan_not_editable",
        "Only a draft rate plan can be edited or published.");

    public static readonly Error PlanModifiedConcurrently = Error.Conflict(
        "pricing.plan_modified_concurrently",
        "The rate plan was modified by someone else in the meantime: reload it.");

    public static readonly Error PlanAlreadyArchived = Error.Conflict(
        "pricing.plan_already_archived",
        "The rate plan is already archived.");

    public static readonly Error PlanInvalid = Error.Validation(
        "pricing.plan_invalid",
        "The rate plan has blocking validation issues and cannot be published.");

    public static readonly Error DraftAlreadyExists = Error.Conflict(
        "pricing.draft_already_exists",
        "A draft already exists for this agency and channel.");

    public static readonly Error PlanNameInvalid = Error.Validation(
        "pricing.plan_name_invalid",
        $"The rate plan name is required ({RatePlan.NameMaxLength} characters max).");

    public static readonly Error SeasonInvalid = Error.Validation(
        "pricing.season_invalid",
        "A season needs a level, a start on or before its end and a minimum duration between 0 and 365 days.");

    public static readonly Error SeasonsOverlap = Error.Validation(
        "pricing.seasons_overlap",
        "Two seasons cover the same date.");

    public static readonly Error EventInvalid = Error.Validation(
        "pricing.event_invalid",
        $"An event needs a name ({PriorityEvent.NameMaxLength} characters max), a start on or before its end, a priority " +
        $"between 1 and {PriorityEvent.MaximumPriority} and a minimum duration between 0 and 365 days.");

    public static readonly Error EventPriorityConflict = Error.Validation(
        "pricing.event_priority_conflict",
        "Two overlapping events have the same priority.");

    public static readonly Error PriceInvalid = Error.Validation(
        "pricing.price_invalid",
        "Daily prices are positive TND amounts (millimes at most) for an existing category and period.");

    public static readonly Error PriceDuplicate = Error.Validation(
        "pricing.price_duplicate",
        "A category has two prices for the same period.");

    public static readonly Error BasePriceMissing = Error.Validation(
        "pricing.base_price_missing",
        "Every priced category needs a base price.");

    public static readonly Error TierInvalid = Error.Validation(
        "pricing.tier_invalid",
        "Duration tiers start at day 1, their starting days strictly increase and their discounts are between 0 and 99.99 %.");

    public static readonly Error TierOverrideInvalid = Error.Validation(
        "pricing.tier_override_invalid",
        "A category discount refers to an existing tier once per category, with a discount between 0 and 99.99 %.");

    public static readonly Error ExtraInvalid = Error.Validation(
        "pricing.extra_invalid",
        "An extra needs a code, a name, a non-negative price, a cap only when charged per day, a location only for a " +
        "delivery and a per-rental charge for deliveries, after-hours and one-way fees.");

    public static readonly Error ExtraCodeDuplicate = Error.Validation(
        "pricing.extra_code_duplicate",
        "Two extras have the same code.");

    public static readonly Error ExtraKindDuplicate = Error.Validation(
        "pricing.extra_kind_duplicate",
        "Only one active extra of each automatic kind (and one delivery per location) is allowed.");

    public static readonly Error PromotionInvalid = Error.Validation(
        "pricing.promotion_invalid",
        "A promotion needs a name, a code (code promotions) or a minimum number of days (early booking, long stay), " +
        "a discount (percentage up to 100 % or positive amount) and date ranges in order.");

    public static readonly Error PromotionCodeDuplicate = Error.Validation(
        "pricing.promotion_code_duplicate",
        "Two promotions have the same code.");

    public static readonly Error ChannelRuleInvalid = Error.Validation(
        "pricing.channel_rule_invalid",
        "One rule per channel; a markup is a percentage above 0 and up to 100 %, the other modes have none.");

    public static readonly Error ChannelRulesOnChannelPlan = Error.Validation(
        "pricing.channel_rules_on_channel_plan",
        "Channel rules belong to the default plan of the agency, not to a channel plan.");

    public static readonly Error AmountInvalid = Error.Validation(
        "pricing.amount_invalid",
        "An amount has more decimals than the millime allows.");

    public static readonly Error SimulationChannelMismatch = Error.Validation(
        "pricing.simulation_channel_mismatch",
        "A channel plan can only be simulated for its own channel.");

    /// <summary>
    /// The stay is shorter than the largest minimum duration of the seasons and events it touches. The required number of
    /// days is part of the message (<see cref="TryGetRequiredMinimumDays"/> reads it back).
    /// </summary>
    public static Error MinimumDurationNotMet(int requiredDays) => Error.Validation(
        MinimumDurationNotMetCode,
        string.Create(CultureInfo.InvariantCulture, $"The stay is too short for these dates (minimum: {requiredDays} days)."));

    /// <summary>Reads the required number of days of a <see cref="MinimumDurationNotMet"/> error.</summary>
    public static bool TryGetRequiredMinimumDays(Error error, out int requiredDays)
    {
        ArgumentNullException.ThrowIfNull(error);
        requiredDays = 0;
        if (error.Code != MinimumDurationNotMetCode)
        {
            return false;
        }

        const string marker = "(minimum: ";
        var start = error.Message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += marker.Length;
        var end = error.Message.IndexOf(' ', start);
        return end > start
            && int.TryParse(error.Message.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out requiredDays);
    }
}
