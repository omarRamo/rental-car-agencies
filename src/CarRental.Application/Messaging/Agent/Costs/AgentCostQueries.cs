using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using CarRental.Domain.Messaging.Agent;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Messaging.Agent.Costs;

/// <summary>
/// One month of the WhatsApp agent (Tunis calendar): turns, model and tool calls, tokens and the share of the prompt
/// read from the cache, AI cost and estimated Meta cost in USD, hand-overs and replies stopped by a guardrail.
/// </summary>
public sealed record AgentMonthSummary(
    int Year,
    int Month,
    int Turns,
    int ModelCalls,
    int ToolCalls,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    decimal AiCostUsd,
    decimal MetaCostUsd,
    int HandOvers,
    int Guardrails)
{
    public decimal CacheHitRatio => new ModelTokenUsage(InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens).CacheHitRatio;

    public decimal TotalCostUsd => AiCostUsd + MetaCostUsd;
}

/// <summary>What the agent cost on the conversations linked to one rental file (by the agency), cumulated.</summary>
public sealed record AgentBookingCost(Guid BookingId, string? BookingNumber, int Conversations, int BotTurns, decimal AiCostUsd, decimal MetaCostUsd)
{
    public decimal TotalCostUsd => AiCostUsd + MetaCostUsd;
}

/// <summary>
/// The cost reports of the WhatsApp agent (permission <c>reports.read</c>): by month from its turn records, and by rental
/// file from the usage of the conversations linked to it (conception phase 4 §2: « cumulés par conversation et par
/// dossier attribué » — the cost of one conversation is shown in the inbox).
/// </summary>
public sealed class AgentCostQueries(IAppDbContext db, IUserContext user, TimeProvider clock, IBookingsQueries bookings)
{
    public const int DefaultMonths = 12;

    private static readonly AgentTurnOutcome[] HandOverOutcomes =
    [
        AgentTurnOutcome.TransferredByModel, AgentTurnOutcome.BlockedByGuardrail, AgentTurnOutcome.CapReached, AgentTurnOutcome.Refused,
        AgentTurnOutcome.Truncated, AgentTurnOutcome.Failed,
    ];

    /// <summary>The last <paramref name="months"/> months (the current one first), months without turns included.</summary>
    public async Task<Result<IReadOnlyList<AgentMonthSummary>>> MonthlyAsync(int months, CancellationToken cancellationToken)
    {
        if (!user.HasPermission(Permissions.ReportsRead))
        {
            return MessagingErrors.Forbidden;
        }

        months = Math.Clamp(months, 1, 36);
        var today = TunisTime.Today(clock);
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1));
        var since = TunisTime.StartOfDay(first);
        var records = await db.Set<AgentTurnRecord>()
            .AsNoTracking()
            .Where(r => r.OccurredAtUtc >= since)
            .Select(r => new
            {
                r.OccurredAtUtc,
                r.Outcome,
                r.ModelCalls,
                r.ToolCalls,
                r.InputTokens,
                r.OutputTokens,
                r.CacheReadTokens,
                r.CacheWriteTokens,
                r.AiCostUsd,
                r.MetaCostUsd,
            })
            .ToListAsync(cancellationToken);

        var byMonth = records
            .GroupBy(r => { var date = TunisTime.DateOf(r.OccurredAtUtc); return (date.Year, date.Month); })
            .ToDictionary(g => g.Key);
        var summaries = new List<AgentMonthSummary>(months);
        for (var month = new DateOnly(today.Year, today.Month, 1); month >= first; month = month.AddMonths(-1))
        {
            var rows = byMonth.TryGetValue((month.Year, month.Month), out var group) ? group.ToList() : [];
            summaries.Add(new AgentMonthSummary(
                month.Year,
                month.Month,
                rows.Count(r => r.Outcome != AgentTurnOutcome.Retried),
                rows.Sum(r => r.ModelCalls),
                rows.Sum(r => r.ToolCalls),
                rows.Sum(r => r.InputTokens),
                rows.Sum(r => r.OutputTokens),
                rows.Sum(r => r.CacheReadTokens),
                rows.Sum(r => r.CacheWriteTokens),
                rows.Sum(r => r.AiCostUsd),
                rows.Sum(r => r.MetaCostUsd),
                rows.Count(r => HandOverOutcomes.Contains(r.Outcome)),
                rows.Count(r => r.Outcome is AgentTurnOutcome.BlockedByGuardrail or AgentTurnOutcome.ReplacedByGuardrail)));
        }

        return summaries;
    }

    /// <summary>The rental files whose conversations the agent answered, the most expensive first.</summary>
    public async Task<Result<PagedResult<AgentBookingCost>>> ByBookingAsync(PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!user.HasPermission(Permissions.ReportsRead))
        {
            return MessagingErrors.Forbidden;
        }

        var byBooking = db.Set<Conversation>()
            .AsNoTracking()
            .Where(c => c.BookingId != null && c.Usage.BotTurns > 0)
            .GroupBy(c => c.BookingId!.Value)
            .Select(g => new
            {
                BookingId = g.Key,
                Conversations = g.Count(),
                BotTurns = g.Sum(c => c.Usage.BotTurns),
                AiCostUsd = g.Sum(c => c.Usage.AiCostUsd),
                MetaCostUsd = g.Sum(c => c.Usage.MetaCostUsd),
            });
        var total = await byBooking.CountAsync(cancellationToken);
        var rows = await byBooking
            .OrderByDescending(r => r.AiCostUsd + r.MetaCostUsd)
            .ThenBy(r => r.BookingId)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);
        var items = new List<AgentBookingCost>(rows.Count);
        foreach (var row in rows)
        {
            var booking = await bookings.GetSummaryAsync(row.BookingId, cancellationToken);
            items.Add(new AgentBookingCost(row.BookingId, booking?.Number, row.Conversations, row.BotTurns, row.AiCostUsd, row.MetaCostUsd));
        }

        return new PagedResult<AgentBookingCost>(items, total, page.Page, page.PageSize);
    }
}
