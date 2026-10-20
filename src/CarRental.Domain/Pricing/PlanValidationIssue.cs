namespace CarRental.Domain.Pricing;

/// <summary>
/// A finding of <see cref="RatePlan.Validate"/>: <see cref="Code"/> (see <see cref="PlanValidationCodes"/>), whether it
/// blocks the publication, and invariant arguments for the localized message (dates as <c>yyyy-MM-dd</c>, names).
/// </summary>
public sealed record PlanValidationIssue(string Code, bool IsBlocking, IReadOnlyList<string> Arguments);

/// <summary>Codes of the validation issues of a rate plan (stable: the back office shows <c>Plan.Issue.{code}</c>).</summary>
public static class PlanValidationCodes
{
    /// <summary>Blocking: no category has a base price.</summary>
    public const string NoPrice = "no_price";

    /// <summary>Warning: the plan has no season (every date at the base price).</summary>
    public const string NoSeason = "no_season";

    /// <summary>Warning: dates between two seasons (arguments: first and last date) are priced at the base price.</summary>
    public const string SeasonGap = "season_gap";

    /// <summary>Warning: an event (argument: its name) has no price, so it only imposes its minimum duration.</summary>
    public const string EventWithoutPrice = "event_without_price";

    /// <summary>Warning (checked by the application): a channel uses its own grid but none is published (argument: channel).</summary>
    public const string ChannelPlanMissing = "channel_plan_missing";
}
