using CarRental.Application.Bookings.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;

namespace CarRental.Application.Reporting.Indicators;

/// <summary>
/// The handling of the web and WhatsApp requests (SPEC §8 "Délai de validation, taux de refus : médiane, pourcentage";
/// SPEC §2 target "&lt; 2 h ouvrées"; ADR-0046), from their timeline. The decision is the first validation (to
/// <c>Confirmed</c> or <c>AwaitingPayment</c>) or refusal; the delay is the time the request spent in
/// <c>PendingValidation</c> before it — the time waiting for documents asked to the customer
/// (<c>InformationRequested</c>) is the customer's, not the agency's — in clock time and within the opening hours.
/// Requests still waiting, expired without decision or cancelled by the customer have no delay.
/// </summary>
internal static class RequestQualityCalculator
{
    /// <summary>Two opening hours (SPEC §2).</summary>
    public static readonly TimeSpan Target = TimeSpan.FromHours(2);

    public static RequestQualityReport Compute(IEnumerable<BookingFactsInfo> requests, TimeOnly opensAt, TimeOnly closesAt)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var handled = requests
            .Where(r => r.Channel is SalesChannel.Web or SalesChannel.WhatsApp)
            .Select(r => (Request: r, Outcome: Handle(r, opensAt, closesAt)))
            .ToList();
        var channels = new[] { SalesChannel.Web, SalesChannel.WhatsApp }
            .Select(channel => Row(channel, handled.Where(h => h.Request.Channel == channel).Select(h => h.Outcome)))
            .ToList();
        return new RequestQualityReport(channels, Row(null, handled.Select(h => h.Outcome)), Target);
    }

    /// <summary>How one request was handled, and its delays when it was decided.</summary>
    public static RequestOutcome Handle(BookingFactsInfo request, TimeOnly opensAt, TimeOnly closesAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        var history = request.StatusHistory.OrderBy(c => c.AtUtc).ToList();
        var decisionIndex = history.FindIndex(IsDecision);
        if (decisionIndex < 0)
        {
            var outcome = request.Status switch
            {
                BookingStatusInfo.Expired => RequestOutcomeKind.Expired,
                BookingStatusInfo.Cancelled => RequestOutcomeKind.Cancelled,
                _ => RequestOutcomeKind.Pending,
            };
            return new RequestOutcome(outcome, null, null);
        }

        var decision = history[decisionIndex];
        var delay = TimeSpan.Zero;
        var businessDelay = TimeSpan.Zero;
        for (var i = 0; i < decisionIndex; i++)
        {
            if (history[i].To != BookingStatusInfo.PendingValidation)
            {
                continue;
            }

            var start = history[i].AtUtc;
            var end = history[i + 1].AtUtc;
            if (end > start)
            {
                delay += end - start;
                businessDelay += BusinessHours.Between(start, end, opensAt, closesAt);
            }
        }

        return new RequestOutcome(
            decision.To == BookingStatusInfo.Refused ? RequestOutcomeKind.Refused : RequestOutcomeKind.Validated,
            delay,
            businessDelay);
    }

    private static bool IsDecision(BookingStatusChangeInfo change) =>
        change.From is BookingStatusInfo.PendingValidation or BookingStatusInfo.InformationRequested
        && change.To is BookingStatusInfo.Confirmed or BookingStatusInfo.AwaitingPayment or BookingStatusInfo.Refused;

    private static RequestQualityRow Row(SalesChannel? channel, IEnumerable<RequestOutcome> outcomes)
    {
        var list = outcomes.ToList();
        var decided = list.Where(o => o.Kind is RequestOutcomeKind.Validated or RequestOutcomeKind.Refused).ToList();
        return new RequestQualityRow(
            channel,
            list.Count,
            list.Count(o => o.Kind == RequestOutcomeKind.Validated),
            list.Count(o => o.Kind == RequestOutcomeKind.Refused),
            list.Count(o => o.Kind == RequestOutcomeKind.Expired),
            list.Count(o => o.Kind == RequestOutcomeKind.Cancelled),
            list.Count(o => o.Kind == RequestOutcomeKind.Pending),
            KpiMath.Median(decided.Select(o => o.Delay!.Value)),
            KpiMath.Median(decided.Select(o => o.BusinessDelay!.Value)),
            decided.Count(o => o.BusinessDelay <= Target));
    }
}

/// <summary>What became of a request.</summary>
internal enum RequestOutcomeKind
{
    Pending = 0,
    Validated = 1,
    Refused = 2,
    Expired = 3,
    Cancelled = 4,
}

/// <summary>The outcome of a request and, once decided, its delays (clock time and opening hours).</summary>
internal sealed record RequestOutcome(RequestOutcomeKind Kind, TimeSpan? Delay, TimeSpan? BusinessDelay);
