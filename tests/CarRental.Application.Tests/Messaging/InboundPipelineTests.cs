using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Inbox;
using CarRental.Application.Messaging.Webhooks;
using CarRental.Application.Tests.Messaging.Fakes;
using CarRental.Domain.Messaging;

namespace CarRental.Application.Tests.Messaging;

/// <summary>
/// From a webhook delivery to the bot: the conversation of the number (linked to the person who has it), the message
/// once per Meta id, one responder turn per customer turn, the reply sent once — and silence after a takeover.
/// </summary>
public sealed class InboundPipelineTests : IAsyncDisposable
{
    private const string Customer = "21698123456";

    private readonly MessagingHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task A_first_message_opens_a_conversation_linked_to_the_person_with_this_phone()
    {
        var personId = _harness.AddPerson("+21698123456");
        var customerId = Guid.CreateVersion7();
        _harness.Customers.IndividualCustomers[personId] = customerId;

        await _harness.CustomerWritesAsync(Customer, "Bonjour, une voiture pour demain ?");

        var conversation = _harness.Conversations.ShouldHaveSingleItem();
        conversation.WhatsAppNumber.ShouldBe("+21698123456");
        conversation.ProfileName.ShouldBe("Hédi");
        conversation.PersonId.ShouldBe(personId);
        conversation.CustomerId.ShouldBe(customerId);
        conversation.Mode.ShouldBe(ConversationMode.Bot);
        conversation.UnreadCount.ShouldBe(1);
        conversation.IsWindowOpen(_harness.Clock.GetUtcNow()).ShouldBeTrue();
        var message = _harness.Messages.ShouldHaveSingleItem();
        message.Text.ShouldBe("Bonjour, une voiture pour demain ?");
        message.Direction.ShouldBe(MessageDirection.Inbound);
    }

    [Fact]
    public async Task Relatives_sharing_a_number_are_left_for_the_agent_to_choose()
    {
        _harness.AddPerson("+21698123456", firstName: "Hédi");
        _harness.AddPerson("+21698123456", firstName: "Amine");

        await _harness.CustomerWritesAsync(Customer, "Salam");

        _harness.Conversations.ShouldHaveSingleItem().PersonId.ShouldBeNull();
        var details = await _harness.RunAsync<WhatsAppInboxQueries, InboxConversationDetails>(
            async q => (await q.GetAsync(_harness.Conversations[0].Id, TestContext.Current.CancellationToken)).Value,
            MessagingHarness.Agent());
        details.PersonCandidates.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_redelivered_message_is_recorded_once_and_answered_once()
    {
        var message = new InboundWhatsAppMessage("wamid.same", Customer, "Hédi", _harness.Clock.GetUtcNow(), TextContent.Received("Bonjour"));

        await _harness.DeliverAsync([message]);
        await _harness.DeliverAsync([message]);
        await _harness.DeliverAsync([message, message]);

        _harness.Messages.ShouldHaveSingleItem();
        _harness.Conversations.ShouldHaveSingleItem().UnreadCount.ShouldBe(1);
        _harness.Responder.Turns.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_responder_gets_the_turn_and_its_single_reply_is_sent()
    {
        _harness.Responder.Reply = turn => new ConversationReply
        {
            Message = new OutboundButtons(
                "Bonjour ! Quelle catégorie ?",
                [new ReplyButtonInfo("cat:ECMR", "Économique"), new ReplyButtonInfo("cat:IFMR", "SUV")]),
            Need = new ConversationNeedInfo(PickupPlace: "TUN", Passengers: 4, Gearbox: GearboxPreferenceInfo.Automatic),
            Usage = new ConversationTurnUsageInfo(1500, 120, CacheReadTokens: 1200, ToolCalls: 1, AiCostUsd: 0.0002m, MetaCostUsd: 0.004m),
        };

        await _harness.CustomerWritesAsync(Customer, "Bonjour, 4 personnes, boîte automatique, à l'aéroport");

        var turn = _harness.Responder.Turns.ShouldHaveSingleItem();
        turn.WhatsAppNumber.ShouldBe("+21698123456");
        turn.ProfileName.ShouldBe("Hédi");
        turn.NewMessages.ShouldHaveSingleItem().Text.ShouldBe("Bonjour, 4 personnes, boîte automatique, à l'aéroport");
        turn.History.ShouldBeEmpty();
        turn.WindowClosesAtUtc.ShouldBe(_harness.Messages.Single(m => m.IsInbound).OccurredAtUtc.AddHours(24));
        turn.Need.ShouldBe(ConversationNeedInfo.Empty);

        var sent = _harness.WhatsApp.Sent.ShouldHaveSingleItem();
        sent.To.ShouldBe(Customer);
        sent.Content.ShouldBeOfType<ButtonsContent>().Buttons.Select(b => b.Title).ShouldBe(["Économique", "SUV"]);
        var reply = _harness.Messages.Single(m => m.Direction == MessageDirection.Outbound);
        reply.Author.ShouldBe(MessageAuthor.Bot);
        reply.Status.ShouldBe(MessageStatus.Accepted);
        reply.MetaMessageId.ShouldBe("wamid.test1");
        _harness.Messages.Single(m => m.IsInbound).BotTurn.ShouldBe(BotTurnState.Replied);

        var conversation = _harness.Conversations.ShouldHaveSingleItem();
        conversation.Need.PickupPlace.ShouldBe("TUN");
        conversation.Need.Passengers.ShouldBe(4);
        conversation.Need.Gearbox.ShouldBe(GearboxPreference.Automatic);
        conversation.Usage.BotTurns.ShouldBe(1);
        conversation.Usage.InputTokens.ShouldBe(1500);
        conversation.Usage.AiCostUsd.ShouldBe(0.0002m);
        conversation.Usage.MetaCostUsd.ShouldBe(0.004m);
    }

    [Fact]
    public async Task The_next_turn_carries_the_history_and_the_need_so_far()
    {
        _harness.Responder.Reply = turn => new ConversationReply
        {
            Message = new OutboundText(turn.History.Count == 0 ? "Pour quelles dates ?" : "Merci, je cherche."),
            Need = turn.Need with { PickupPlace = "TUN" },
        };

        await _harness.CustomerWritesAsync(Customer, "Bonjour");
        _harness.Clock.Advance(TimeSpan.FromMinutes(2));
        await _harness.CustomerWritesAsync(Customer, "Du 3 au 7 juillet");

        _harness.Responder.Turns.Count.ShouldBe(2);
        var second = _harness.Responder.Turns[1];
        second.History.Select(m => (m.Author, m.Text)).ShouldBe(
        [
            (MessageAuthorInfo.Customer, "Bonjour"),
            (MessageAuthorInfo.Bot, "Pour quelles dates ?"),
        ]);
        second.NewMessages.ShouldHaveSingleItem().Text.ShouldBe("Du 3 au 7 juillet");
        second.Need.PickupPlace.ShouldBe("TUN");
        _harness.WhatsApp.Sent.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Messages_received_in_a_burst_are_answered_once_by_the_turn_of_the_latest()
    {
        var at = _harness.Clock.GetUtcNow();
        _harness.Responder.Reply = _ => new ConversationReply { Message = new OutboundText("Bien noté !") };

        await _harness.DeliverAsync(
        [
            new InboundWhatsAppMessage("wamid.1", Customer, "Hédi", at, TextContent.Received("Bonjour")),
            new InboundWhatsAppMessage("wamid.2", Customer, "Hédi", at.AddSeconds(5), TextContent.Received("Pour demain matin")),
            new InboundWhatsAppMessage("wamid.3", Customer, "Hédi", at.AddSeconds(9), TextContent.Received("4 personnes")),
        ]);

        var turn = _harness.Responder.Turns.ShouldHaveSingleItem();
        turn.NewMessages.Select(m => m.Text).ShouldBe(["Bonjour", "Pour demain matin", "4 personnes"]);
        _harness.WhatsApp.Sent.ShouldHaveSingleItem();
        _harness.Messages.Where(m => m.IsInbound).Select(m => m.BotTurn)
            .ShouldBe([BotTurnState.Superseded, BotTurnState.Superseded, BotTurnState.Replied], ignoreOrder: true);
    }

    [Fact]
    public async Task With_the_default_responder_the_bot_stays_silent()
    {
        _harness.Responder.Reply = _ => ConversationReply.None;

        await _harness.CustomerWritesAsync(Customer, "Bonjour");

        _harness.WhatsApp.Sent.ShouldBeEmpty();
        _harness.Messages.ShouldHaveSingleItem().BotTurn.ShouldBe(BotTurnState.NoReply);
        _harness.Conversations[0].Usage.BotTurns.ShouldBe(0);
    }

    [Fact]
    public async Task A_conversation_taken_over_never_reaches_the_responder()
    {
        await _harness.CustomerWritesAsync(Customer, "Bonjour");
        var agent = MessagingHarness.Agent();
        (await _harness.RunAsync<WhatsAppInboxService, CarRental.Domain.Common.Result>(s => s.TakeOverAsync(_harness.Conversations[0].Id, TestContext.Current.CancellationToken), agent))
            .IsSuccess.ShouldBeTrue();

        await _harness.CustomerWritesAsync(Customer, "Je voudrais parler à quelqu'un");

        _harness.Responder.Turns.ShouldHaveSingleItem();
        _harness.Messages.Where(m => m.IsInbound).Last().BotTurn.ShouldBe(BotTurnState.None);
    }

    [Fact]
    public async Task A_takeover_during_the_responder_call_drops_its_reply()
    {
        var agent = MessagingHarness.Agent();
        _harness.Responder.During = async turn =>
            (await _harness.RunAsync<WhatsAppInboxService, CarRental.Domain.Common.Result>(s => s.TakeOverAsync(turn.ConversationId, TestContext.Current.CancellationToken), agent))
                .IsSuccess.ShouldBeTrue();
        _harness.Responder.Reply = _ => new ConversationReply
        {
            Message = new OutboundText("Réponse du bot qui ne doit pas partir"),
            Usage = new ConversationTurnUsageInfo(800, 60, AiCostUsd: 0.0001m),
        };

        await _harness.CustomerWritesAsync(Customer, "Bonjour");

        _harness.WhatsApp.Sent.ShouldBeEmpty();
        _harness.Messages.ShouldHaveSingleItem().BotTurn.ShouldBe(BotTurnState.Silenced);
        var conversation = _harness.Conversations.ShouldHaveSingleItem();
        conversation.Mode.ShouldBe(ConversationMode.Human);
        conversation.Usage.BotTurns.ShouldBe(1, "the model call happened: its cost counts");
    }

    [Fact]
    public async Task A_takeover_cancels_the_bot_reply_still_queued()
    {
        var queued = await BotReplyQueuedAsync();

        await _harness.RunAsync<WhatsAppInboxService, CarRental.Domain.Common.Result>(
            s => s.TakeOverAsync(_harness.Conversations[0].Id, TestContext.Current.CancellationToken),
            MessagingHarness.Agent());

        var reply = _harness.Messages.Single(m => m.Id == queued.MessageId);
        reply.Status.ShouldBe(MessageStatus.Cancelled, "cancelled by the takeover itself, before the outbox sends it");
        reply.FailureCode.ShouldBe(MessagingErrors.BotSilenced.Code);
        await _harness.HandleAsync(queued);
        _harness.WhatsApp.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bot_reply_is_checked_again_when_it_should_leave()
    {
        var queued = await BotReplyQueuedAsync();

        // An agent holds the conversation by the time the outbox sends the reply (the takeover did not see it queued).
        _harness.Conversations[0].TakeOver(new CarRental.Domain.Common.AuditActor(Guid.CreateVersion7(), "agent@demo.local", null, null), _harness.Clock.GetUtcNow());
        await _harness.HandleAsync(queued);

        _harness.WhatsApp.Sent.ShouldBeEmpty();
        var reply = _harness.Messages.Single(m => m.Id == queued.MessageId);
        reply.Status.ShouldBe(MessageStatus.Cancelled);
        reply.FailureCode.ShouldBe(MessagingErrors.BotSilenced.Code);
    }

    [Fact]
    public async Task A_takeover_handed_back_during_the_responder_call_still_drops_the_stale_reply()
    {
        var agent = MessagingHarness.Agent();
        _harness.Responder.During = async turn =>
        {
            (await AsAgentAsync(s => s.TakeOverAsync(turn.ConversationId, TestContext.Current.CancellationToken))).IsSuccess.ShouldBeTrue();
            (await AsAgentAsync(s => s.SendTextAsync(turn.ConversationId, "Bonjour, ici l'agence.", TestContext.Current.CancellationToken))).IsSuccess.ShouldBeTrue();
            (await AsAgentAsync(s => s.HandBackToBotAsync(turn.ConversationId, TestContext.Current.CancellationToken))).IsSuccess.ShouldBeTrue();
        };
        _harness.Responder.Reply = _ => new ConversationReply
        {
            Message = new OutboundText("Réponse périmée du bot"),
            Usage = new ConversationTurnUsageInfo(900, 40, AiCostUsd: 0.0002m),
        };

        await _harness.CustomerWritesAsync(Customer, "Bonjour");

        _harness.WhatsApp.Sent.ShouldHaveSingleItem().Content.ShouldBeOfType<TextContent>().Body.ShouldBe("Bonjour, ici l'agence.");
        _harness.Messages.Single(m => m.IsInbound).BotTurn.ShouldBe(BotTurnState.Silenced);
        var conversation = _harness.Conversations.ShouldHaveSingleItem();
        conversation.Mode.ShouldBe(ConversationMode.Bot);
        conversation.Usage.BotTurns.ShouldBe(1, "the model call happened: its cost counts");
        conversation.Usage.AiCostUsd.ShouldBe(0.0002m);

        Task<CarRental.Domain.Common.Result> AsAgentAsync(Func<WhatsAppInboxService, Task<CarRental.Domain.Common.Result>> action) =>
            _harness.RunAsync<WhatsAppInboxService, CarRental.Domain.Common.Result>(action, agent);
    }

    [Fact]
    public async Task The_bot_hands_over_after_its_last_message_and_then_keeps_quiet()
    {
        _harness.Responder.Reply = _ => new ConversationReply
        {
            Message = new OutboundText("Un agent vous répond bientôt."),
            HandOverReason = "Réclamation sur une caution",
        };

        await _harness.CustomerWritesAsync(Customer, "Ma caution n'est toujours pas rendue !");
        await _harness.CustomerWritesAsync(Customer, "Allô ?");

        _harness.Responder.Turns.ShouldHaveSingleItem();
        _harness.WhatsApp.Sent.ShouldHaveSingleItem().Content.ShouldBeOfType<TextContent>().Body.ShouldBe("Un agent vous répond bientôt.");
        var conversation = _harness.Conversations.ShouldHaveSingleItem();
        conversation.Mode.ShouldBe(ConversationMode.Human);
        conversation.HandoverReason.ShouldBe("Réclamation sur une caution");
        conversation.HumanAgentId.ShouldBeNull();
    }

    [Fact]
    public async Task An_invalid_reply_of_the_responder_is_not_sent()
    {
        _harness.Responder.Reply = _ => new ConversationReply
        {
            Message = new OutboundButtons("Choisissez", [new("a", "A"), new("b", "B"), new("c", "C"), new("d", "D")]),
            Need = new ConversationNeedInfo(Passengers: 0),
        };

        await _harness.CustomerWritesAsync(Customer, "Bonjour");

        _harness.WhatsApp.Sent.ShouldBeEmpty();
        _harness.Messages.ShouldHaveSingleItem().BotTurn.ShouldBe(BotTurnState.NoReply);
        _harness.Conversations[0].Need.ShouldBe(ConversationNeed.Empty);
    }

    [Fact]
    public async Task A_media_reaches_the_responder_as_its_kind_only()
    {
        await _harness.DeliverAsync(
            [new InboundWhatsAppMessage("wamid.photo", Customer, null, _harness.Clock.GetUtcNow(), MediaContent.Received(MediaKind.Image, "987654321"))]);

        var message = _harness.Responder.Turns.ShouldHaveSingleItem().NewMessages.ShouldHaveSingleItem();
        message.Kind.ShouldBe(MessageKindInfo.Media);
        message.MediaKind.ShouldBe(MediaKindInfo.Image);
        message.Text.ShouldBeNull();
    }

    [Fact]
    public async Task A_turn_processed_after_the_window_closed_gets_no_reply()
    {
        _harness.Responder.Reply = _ => new ConversationReply { Message = new OutboundText("Trop tard") };
        var payload = $"payload-{Guid.NewGuid():N}";
        _harness.Parser.Batches[payload] = new WhatsAppWebhookBatch(
            [new InboundWhatsAppMessage("wamid.old", Customer, null, _harness.Clock.GetUtcNow(), TextContent.Received("Bonjour"))],
            [],
            0);
        await _harness.HandleAsync(new WhatsAppWebhookReceived(payload, _harness.Clock.GetUtcNow()));
        var turnEvent = _harness.Db.PublishedEvents.OfType<CustomerMessageReceived>().Single();
        _harness.Db.PublishedEvents.Clear();

        _harness.Clock.Advance(TimeSpan.FromHours(25));
        await _harness.HandleAsync(turnEvent);

        _harness.Responder.Turns.ShouldBeEmpty();
        _harness.Messages.ShouldHaveSingleItem().BotTurn.ShouldBe(BotTurnState.NoReply);
    }

    [Fact]
    public async Task An_unreadable_delivery_is_dropped_without_failing()
    {
        await _harness.HandleAsync(new WhatsAppWebhookReceived("{not json", _harness.Clock.GetUtcNow()));

        _harness.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_message_without_a_usable_meta_id_is_ignored()
    {
        var now = _harness.Clock.GetUtcNow();

        await _harness.DeliverAsync(
        [
            new InboundWhatsAppMessage(" ", Customer, null, now, TextContent.Received("Bonjour")),
            new InboundWhatsAppMessage(new string('w', Message.MetaMessageIdMaxLength + 1), Customer, null, now, TextContent.Received("Bonjour")),
        ]);
        await _harness.DeliverAsync([new InboundWhatsAppMessage(new string('w', Message.MetaMessageIdMaxLength + 1), Customer, null, now, TextContent.Received("Bonjour"))]);

        _harness.Messages.ShouldBeEmpty();
        _harness.Responder.Turns.ShouldBeEmpty();
    }

    /// <summary>A customer writes, the bot answers: its reply is queued (not sent yet).</summary>
    private async Task<OutboundMessageQueued> BotReplyQueuedAsync()
    {
        _harness.Responder.Reply = _ => new ConversationReply { Message = new OutboundText("Bonjour !") };
        var payload = $"payload-{Guid.NewGuid():N}";
        _harness.Parser.Batches[payload] = new WhatsAppWebhookBatch(
            [new InboundWhatsAppMessage("wamid.x", Customer, null, _harness.Clock.GetUtcNow(), TextContent.Received("Bonjour"))],
            [],
            0);
        await _harness.HandleAsync(new WhatsAppWebhookReceived(payload, _harness.Clock.GetUtcNow()));
        var turnEvent = _harness.Db.PublishedEvents.OfType<CustomerMessageReceived>().Single();
        _harness.Db.PublishedEvents.Clear();
        await _harness.HandleAsync(turnEvent);
        var queued = _harness.Db.PublishedEvents.OfType<OutboundMessageQueued>().Single();
        _harness.Db.PublishedEvents.Clear();
        return queued;
    }
}
