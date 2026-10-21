using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Messaging.Agent;
using CarRental.Application.Messaging.Agent.Costs;
using CarRental.Application.Tests.Messaging.Agent.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using CarRental.Domain.Messaging.Agent;

namespace CarRental.Application.Tests.Messaging.Agent;

/// <summary>
/// What the agent costs: every turn records its calls, tokens and costs (the retried attempts too), priced from the grid
/// of the platform settings; the monthly summary adds them up by Tunis month, with the cache hit ratio, the hand-overs
/// and the replies stopped by a guardrail.
/// </summary>
public sealed class AgentCostTests : IAsyncDisposable
{
    private readonly AgentHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_turn_is_recorded_with_its_tokens_and_its_cost_from_the_price_grid()
    {
        _harness.Settings.Set(AgentSettings.InputUsdPerMillion, 1m);
        _harness.Settings.Set(AgentSettings.OutputUsdPerMillion, 5m);
        _harness.Model.Then(
            ModelStep.Call("infos_agence", """{"sujet":"horaires"}""") with { InputTokens = 1_000, OutputTokens = 50, CacheReadTokens = 3_000, CacheWriteTokens = 400 },
            ModelStep.Reply("Nous sommes ouverts de 08:00 à 20:00.") with { InputTokens = 1_200, OutputTokens = 40, CacheReadTokens = 3_400, CacheWriteTokens = 0 });

        var reply = await _harness.NewConversation().SendAsync("Quels sont vos horaires ?");

        var record = _harness.Db.Rows<AgentTurnRecord>().ShouldHaveSingleItem();
        record.Outcome.ShouldBe(AgentTurnOutcome.Replied);
        record.Model.ShouldBe("claude-haiku-5-5");
        record.Language.ShouldBe(AgentLanguage.French);
        record.ModelCalls.ShouldBe(2);
        record.ToolCalls.ShouldBe(1);
        record.InputTokens.ShouldBe(2_200);
        record.OutputTokens.ShouldBe(90);
        record.CacheReadTokens.ShouldBe(6_400);
        record.CacheWriteTokens.ShouldBe(400);
        // 2 200 × 1 + 90 × 5 + 6 400 × 0.1 + 400 × 1.25 = 2 200 + 450 + 640 + 500 = 3 790 per million tokens.
        record.AiCostUsd.ShouldBe(0.00379m);
        var usage = reply.Usage.ShouldNotBeNull();
        usage.AiCostUsd.ShouldBe(0.00379m);
        usage.InputTokens.ShouldBe(2_200);
        usage.CacheReadTokens.ShouldBe(6_400);
        usage.CacheWriteTokens.ShouldBe(400);
    }

    [Fact]
    public async Task The_monthly_summary_adds_the_turns_up_by_tunis_month()
    {
        // 30 June 2027 at 23:30 UTC is already 1 July in Tunis.
        var july = new DateTimeOffset(2027, 6, 30, 23, 30, 0, TimeSpan.Zero);
        var june = new DateTimeOffset(2027, 6, 30, 22, 30, 0, TimeSpan.Zero);
        Record(june, AgentTurnOutcome.Replied, input: 1_000, cacheRead: 3_000, cost: 0.001m);
        Record(july, AgentTurnOutcome.Replied, input: 1_000, cacheRead: 9_000, cost: 0.002m, meta: 0.01m);
        Record(july, AgentTurnOutcome.BlockedByGuardrail, input: 500, cost: 0.0005m);
        Record(july, AgentTurnOutcome.ReplacedByGuardrail, input: 500, cost: 0.0005m);
        Record(july, AgentTurnOutcome.TransferredByModel, input: 500, cost: 0.0005m);
        Record(july, AgentTurnOutcome.Retried, input: 100, cost: 0.0001m);
        _harness.Clock.SetUtcNow(new DateTimeOffset(2027, 7, 15, 10, 0, 0, TimeSpan.Zero));

        var months = (await _harness.RunAsync<AgentCostQueries, Result<IReadOnlyList<AgentMonthSummary>>>(q => q.MonthlyAsync(3, Ct), Reader())).Value;

        months.Select(m => (m.Year, m.Month)).ShouldBe([(2027, 7), (2027, 6), (2027, 5)]);
        var current = months[0];
        current.Turns.ShouldBe(4, "a retried attempt is no turn, but its cost counts");
        current.InputTokens.ShouldBe(2_600);
        current.CacheReadTokens.ShouldBe(9_000);
        current.AiCostUsd.ShouldBe(0.0036m);
        current.MetaCostUsd.ShouldBe(0.01m);
        current.TotalCostUsd.ShouldBe(0.0136m);
        current.HandOvers.ShouldBe(2);
        current.Guardrails.ShouldBe(2);
        current.CacheHitRatio.ShouldBe(9_000m / 11_600m, 0.0001m);
        months[1].Turns.ShouldBe(1);
        months[1].AiCostUsd.ShouldBe(0.001m);
        months[2].Turns.ShouldBe(0);
    }

    [Fact]
    public async Task The_costs_are_cumulated_by_rental_file_the_most_expensive_first()
    {
        var now = _harness.Clock.GetUtcNow();
        var pickupAt = now.AddDays(10);
        var cheap = _harness.Bookings.Add(_harness.World.Agency.Id, _harness.World.Carthage.Id, BookingStatusInfo.Confirmed, pickupAt, pickupAt.AddDays(3), null);
        var expensive = _harness.Bookings.Add(_harness.World.Agency.Id, _harness.World.Carthage.Id, BookingStatusInfo.Confirmed, pickupAt, pickupAt.AddDays(3), null);
        Conversation("+21698000011", cheap.Id, (0.001m, 0.005m));
        Conversation("+21698000012", expensive.Id, (0.004m, 0.01m), (0.002m, 0.01m));
        Conversation("+21698000013", expensive.Id, (0.003m, 0m));
        Conversation("+21698000014", bookingId: null, (0.5m, 0m));
        Conversation("+21698000015", Guid.CreateVersion7());

        var page = (await _harness.RunAsync<AgentCostQueries, Result<PagedResult<AgentBookingCost>>>(q => q.ByBookingAsync(new PageRequest(), Ct), Reader())).Value;

        page.TotalCount.ShouldBe(2, "a conversation without a file, or without a bot turn, counts for no file");
        page.Items.Select(i => i.BookingNumber).ShouldBe([expensive.Number, cheap.Number]);
        var first = page.Items[0];
        first.Conversations.ShouldBe(2);
        first.BotTurns.ShouldBe(3);
        first.AiCostUsd.ShouldBe(0.009m);
        first.MetaCostUsd.ShouldBe(0.02m);
        first.TotalCostUsd.ShouldBe(0.029m);
    }

    [Fact]
    public async Task The_cost_reports_need_the_reports_permission()
    {
        var agent = UserContextSnapshot.ForUser(Guid.CreateVersion7(), "agent@demo.local", [Permissions.WhatsAppInbox]);

        var result = await _harness.RunAsync<AgentCostQueries, Result<IReadOnlyList<AgentMonthSummary>>>(q => q.MonthlyAsync(12, Ct), agent);

        result.Error.ShouldBe(MessagingErrors.Forbidden);
    }

    /// <summary>A conversation linked to <paramref name="bookingId"/> with one bot turn per (AI, Meta) cost.</summary>
    private void Conversation(string number, Guid? bookingId, params (decimal Ai, decimal Meta)[] turns)
    {
        var conversation = Domain.Messaging.Conversation.Start(number, null, _harness.Clock.GetUtcNow()).Value;
        conversation.LinkBooking(bookingId);
        foreach (var (ai, meta) in turns)
        {
            conversation.RecordBotTurn(new BotTurnUsage(1_000, 50, aiCostUsd: ai, metaCostUsd: meta));
        }

        _harness.Db.Set<Domain.Messaging.Conversation>().Add(conversation);
    }

    private static UserContextSnapshot Reader() => UserContextSnapshot.ForUser(Guid.CreateVersion7(), "gerant@demo.local", [Permissions.ReportsRead]);

    private void Record(DateTimeOffset at, AgentTurnOutcome outcome, long input, decimal cost, long cacheRead = 0, decimal meta = 0m) =>
        _harness.Db.Set<AgentTurnRecord>().Add(AgentTurnRecord.Create(
            Guid.CreateVersion7(), at, "claude-haiku-5-5", outcome, AgentLanguage.French, 1, 0, new ModelTokenUsage(input, 50, cacheRead, 0), cost, meta));
}
