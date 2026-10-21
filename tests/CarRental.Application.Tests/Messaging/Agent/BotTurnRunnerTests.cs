using CarRental.Application.Messaging.Agent.Turns;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Inbox;
using CarRental.Application.Tests.Messaging.Fakes;
using CarRental.Domain.Messaging;
using CarRental.Domain.Messaging.Agent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarRental.Application.Tests.Messaging.Agent;

/// <summary>
/// The bot turns out of the outbox (ADR-0054): the handler of a customer message only hands the turn over; the runner
/// answers the pending turn of a conversation once — the whole burst —, under the conversation's lock, keeps the
/// takeover rule (prepare, respond, apply), and on a failure retries with growing delays before a hand-over to a human:
/// a turn is never dropped silently. The queue of the executor is read from the messages themselves.
/// </summary>
public sealed class BotTurnRunnerTests : IAsyncDisposable
{
    private const string Customer = "21698123456";

    private readonly MessagingHarness _harness = new(services => services.Replace(ServiceDescriptor.Singleton<IBotTurnDispatcher, DeferredDispatcher>()));

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid ConversationId => _harness.Conversations.Single().Id;

    [Fact]
    public async Task The_handler_of_a_message_only_hands_the_turn_over()
    {
        await _harness.CustomerWritesAsync(Customer, "Bonjour");

        _harness.Responder.Turns.ShouldBeEmpty();
        _harness.Messages.ShouldHaveSingleItem().BotTurn.ShouldBe(BotTurnState.Pending);
        _harness.Services.GetRequiredService<IBotTurnDispatcher>().ShouldBeOfType<DeferredDispatcher>().Dispatched.ShouldBe([ConversationId]);
    }

    [Fact]
    public async Task The_runner_answers_a_burst_once()
    {
        _harness.Responder.Reply = _ => new ConversationReply { Message = new OutboundText("Bien noté !") };
        await _harness.CustomerWritesAsync(Customer, "Bonjour");
        await _harness.CustomerWritesAsync(Customer, "Pour demain matin");

        (await RunAsync()).ShouldBe(BotTurnRunOutcome.Completed);
        await _harness.DrainAsync();

        _harness.Responder.Turns.ShouldHaveSingleItem().NewMessages.Select(m => m.Text).ShouldBe(["Bonjour", "Pour demain matin"]);
        _harness.WhatsApp.Sent.ShouldHaveSingleItem();
        _harness.Messages.Where(m => m.IsInbound).Select(m => m.BotTurn).ShouldBe([BotTurnState.Superseded, BotTurnState.Replied], ignoreOrder: true);
        (await RunAsync()).ShouldBe(BotTurnRunOutcome.NoTurn, "nothing is pending any more");
        _harness.Responder.Turns.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_conversation_whose_turn_runs_elsewhere_is_left_for_later()
    {
        _harness.Responder.Reply = _ => new ConversationReply { Message = new OutboundText("Bonjour !") };
        await _harness.CustomerWritesAsync(Customer, "Bonjour");
        var turnLock = _harness.Services.GetRequiredService<IConversationTurnLock>();
        var lease = (await turnLock.TryAcquireAsync(ConversationId, Ct)).ShouldNotBeNull();

        (await RunAsync()).ShouldBe(BotTurnRunOutcome.Busy);
        _harness.Responder.Turns.ShouldBeEmpty();
        (await turnLock.TryAcquireAsync(ConversationId, Ct)).ShouldBeNull("one turn at a time per conversation");

        await lease.DisposeAsync();
        (await RunAsync()).ShouldBe(BotTurnRunOutcome.Completed);
    }

    [Fact]
    public async Task A_takeover_during_the_responder_call_wins()
    {
        var agent = MessagingHarness.Agent();
        _harness.Responder.During = async turn =>
            (await _harness.RunAsync<WhatsAppInboxService, Domain.Common.Result>(s => s.TakeOverAsync(turn.ConversationId, Ct), agent)).IsSuccess.ShouldBeTrue();
        _harness.Responder.Reply = _ => new ConversationReply { Message = new OutboundText("Réponse du bot qui ne doit pas partir") };
        await _harness.CustomerWritesAsync(Customer, "Bonjour");

        (await RunAsync()).ShouldBe(BotTurnRunOutcome.Completed);
        await _harness.DrainAsync();

        _harness.WhatsApp.Sent.ShouldBeEmpty();
        _harness.Messages.ShouldHaveSingleItem().BotTurn.ShouldBe(BotTurnState.Silenced);
        _harness.Conversations.Single().Mode.ShouldBe(ConversationMode.Human);
    }

    [Fact]
    public async Task A_failed_turn_is_retried_with_growing_delays_then_handed_over_to_a_human()
    {
        _harness.Responder.Reply = _ => throw new HttpRequestException("503 overloaded");
        await _harness.CustomerWritesAsync(Customer, "Bonjour, une voiture pour demain ?");
        var options = new BotTurnOptions();

        foreach (var delay in new[] { TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5) })
        {
            var failedAt = _harness.Clock.GetUtcNow();
            (await RunAsync()).ShouldBe(BotTurnRunOutcome.RetryScheduled);
            var retry = _harness.Db.Rows<BotTurnRetry>().ShouldHaveSingleItem();
            retry.NextAttemptAtUtc.ShouldNotBeNull().ShouldBeInRange(failedAt + delay, failedAt + delay + TimeSpan.FromSeconds(1));
            _harness.Messages.Single(m => m.IsInbound).BotTurn.ShouldBe(BotTurnState.Pending, "the turn waits for its retry");
            (await DueAsync(options)).ShouldBeEmpty("not before the delay");
            _harness.Clock.Advance(delay);
            (await DueAsync(options)).ShouldBe([ConversationId]);
        }

        (await RunAsync()).ShouldBe(BotTurnRunOutcome.HandedOver);
        await _harness.DrainAsync();

        _harness.Responder.Turns.Count.ShouldBe(options.MaxAttempts);
        var conversation = _harness.Conversations.Single();
        conversation.Mode.ShouldBe(ConversationMode.Human);
        conversation.HandoverReason.ShouldBe(BotTurnRunner.UnavailableReason);
        _harness.WhatsApp.Sent.ShouldHaveSingleItem().Content.ShouldBeOfType<TextContent>().Body.ShouldBe(AgentMessages.HandOver(AgentLanguage.French));
        _harness.Db.Rows<BotTurnRetry>().ShouldBeEmpty();
        (await DueAsync(options)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_success_after_a_failure_clears_the_retry()
    {
        var calls = 0;
        _harness.Responder.Reply = _ => ++calls == 1 ? throw new TimeoutException() : new ConversationReply { Message = new OutboundText("Bonjour !") };
        await _harness.CustomerWritesAsync(Customer, "Bonjour");

        (await RunAsync()).ShouldBe(BotTurnRunOutcome.RetryScheduled);
        (await RunAsync()).ShouldBe(BotTurnRunOutcome.Completed);

        _harness.Db.Rows<BotTurnRetry>().ShouldBeEmpty();
        _harness.Conversations.Single().Mode.ShouldBe(ConversationMode.Bot);
    }

    [Fact]
    public async Task The_queue_waits_until_the_customer_is_quiet()
    {
        var options = new BotTurnOptions { QuietPeriod = TimeSpan.FromSeconds(2) };
        await _harness.CustomerWritesAsync(Customer, "Bonjour");
        var writtenAt = _harness.Messages.Single().CreatedAtUtc;

        (await DueAsync(options)).ShouldBeEmpty();
        (await NextDueAtAsync(options)).ShouldBe(writtenAt + options.QuietPeriod);

        _harness.Clock.Advance(TimeSpan.FromSeconds(2));
        (await DueAsync(options)).ShouldBe([ConversationId]);
        (await DueAsync(options, running: [ConversationId])).ShouldBeEmpty("a turn already running here is not started twice");
        (await NextDueAtAsync(options, excluded: [ConversationId])).ShouldBeNull("a running turn signals its end: no wait on it");
    }

    [Fact]
    public async Task The_queue_serves_the_oldest_conversations_first_and_is_empty_without_pending_turns()
    {
        var options = new BotTurnOptions { QuietPeriod = TimeSpan.Zero };
        (await NextDueAtAsync(options)).ShouldBeNull();
        await _harness.CustomerWritesAsync("21698000001", "Bonjour");
        _harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await _harness.CustomerWritesAsync("21698000002", "Bonjour");
        var first = _harness.Conversations.Single(c => c.WhatsAppNumber == "+21698000001").Id;
        var second = _harness.Conversations.Single(c => c.WhatsAppNumber == "+21698000002").Id;

        (await DueAsync(options)).ShouldBe([first, second]);
        (await DueAsync(options, take: 1)).ShouldBe([first]);
    }

    [Theory]
    [InlineData("00:00:20;00:01:00;00:05:00", 4, new[] { 20d, 60d, 300d, 300d })]
    [InlineData("00:00:05", 2, new[] { 5d, 5d })]
    [InlineData("", 1, new[] { 0d })]
    public void The_retry_delays_set_the_attempts_before_the_hand_over(string delays, int attempts, double[] seconds)
    {
        var options = new BotTurnOptions { RetryDelays = delays };

        options.MaxAttempts.ShouldBe(attempts);
        seconds.Select((s, i) => options.DelayAfter(i + 1)).ShouldBe(seconds.Select(TimeSpan.FromSeconds));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("00:00:20;-00:00:01")]
    [InlineData("00:00:00")]
    public void Unreadable_retry_delays_are_refused(string delays)
    {
        new BotTurnOptions { RetryDelays = delays }.ParsedRetryDelays().ShouldBeNull();
    }

    private Task<BotTurnRunOutcome> RunAsync() => _harness.Services.GetRequiredService<IBotTurnRunner>().RunAsync(ConversationId, Ct);

    private Task<IReadOnlyList<Guid>> DueAsync(BotTurnOptions options, IReadOnlyCollection<Guid>? running = null, int take = 10) =>
        _harness.RunAsync<IBotTurnQueue, IReadOnlyList<Guid>>(q => q.DueAsync(options, running ?? [], take, Ct));

    private Task<DateTimeOffset?> NextDueAtAsync(BotTurnOptions options, IReadOnlyCollection<Guid>? excluded = null) =>
        _harness.RunAsync<IBotTurnQueue, DateTimeOffset?>(q => q.NextDueAtAsync(options, excluded ?? [], Ct));

    /// <summary>Records the turns handed over, as the background executor would receive them.</summary>
    private sealed class DeferredDispatcher : IBotTurnDispatcher
    {
        public List<Guid> Dispatched { get; } = [];

        public Task DispatchAsync(Guid conversationId, Guid messageId, string? correlationId, CancellationToken cancellationToken)
        {
            Dispatched.Add(conversationId);
            return Task.CompletedTask;
        }
    }
}
