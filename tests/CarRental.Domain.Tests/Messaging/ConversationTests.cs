using CarRental.Domain.Common;
using CarRental.Domain.Messaging;

namespace CarRental.Domain.Tests.Messaging;

public sealed class ConversationTests
{
    internal static readonly DateTimeOffset Now = new(2027, 7, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly AuditActor Agent = new(Guid.CreateVersion7(), "agent@demo.local", "203.0.113.5", "corr");

    internal static Conversation NewConversation(string number = "21698123456") => Conversation.Start(number, "Hédi", Now).Value;

    internal static Message Receive(Conversation conversation, string text = "Bonjour", DateTimeOffset? at = null, string? id = null) =>
        conversation.ReceiveCustomerMessage(id ?? $"wamid.{Guid.NewGuid():N}", TextContent.Received(text), at ?? Now, "Hédi", at ?? Now);

    [Fact]
    public void Start_normalizes_the_number_answers_with_the_bot_and_raises_an_event()
    {
        var conversation = Conversation.Start("21698123456", "  Hédi   Ben Salah ", Now).Value;

        conversation.WhatsAppNumber.ShouldBe("+21698123456");
        conversation.ProfileName.ShouldBe("Hédi Ben Salah");
        conversation.Mode.ShouldBe(ConversationMode.Bot);
        conversation.Status.ShouldBe(ConversationStatus.Open);
        conversation.CreatedAtUtc.ShouldBe(Now);
        conversation.LastActivityAtUtc.ShouldBe(Now);
        conversation.LastCustomerMessageAtUtc.ShouldBeNull();
        conversation.WindowClosesAtUtc.ShouldBeNull();
        conversation.IsWindowOpen(Now).ShouldBeFalse();
        conversation.Need.ShouldBe(ConversationNeed.Empty);
        conversation.Usage.ShouldBe(ConversationUsage.Zero);
        conversation.DomainEvents.ShouldHaveSingleItem().ShouldBe(new ConversationStarted(conversation.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("+216 98 123 456")]
    [InlineData("0021698123456")]
    [InlineData("1234567")]
    [InlineData("1234567890123456")]
    [InlineData("abc")]
    public void Start_refuses_a_number_that_is_not_international(string number)
    {
        Conversation.Start(number, null, Now).Error.ShouldBe(MessagingErrors.NumberInvalid);
    }

    [Fact]
    public void A_customer_message_opens_the_window_for_24_hours_from_the_time_it_was_sent()
    {
        var conversation = NewConversation();
        var sentAt = Now.AddMinutes(-10);

        var message = Receive(conversation, "Bonjour, une voiture pour demain ?", sentAt);

        message.Direction.ShouldBe(MessageDirection.Inbound);
        message.Author.ShouldBe(MessageAuthor.Customer);
        message.Status.ShouldBe(MessageStatus.Received);
        message.Text.ShouldBe("Bonjour, une voiture pour demain ?");
        message.OccurredAtUtc.ShouldBe(sentAt);
        conversation.LastCustomerMessageAtUtc.ShouldBe(sentAt);
        conversation.WindowClosesAtUtc.ShouldBe(sentAt.AddHours(24));
        conversation.IsWindowOpen(sentAt.AddHours(24).AddTicks(-1)).ShouldBeTrue();
        conversation.IsWindowOpen(sentAt.AddHours(24)).ShouldBeFalse();
        conversation.UnreadCount.ShouldBe(1);
    }

    [Fact]
    public void A_late_webhook_never_moves_the_window_back_and_a_clock_ahead_never_extends_it()
    {
        var conversation = NewConversation();
        Receive(conversation, "second", Now);

        Receive(conversation, "first, delivered late", Now.AddHours(-2));
        conversation.LastCustomerMessageAtUtc.ShouldBe(Now);

        conversation.ReceiveCustomerMessage("wamid.future", TextContent.Received("ahead"), Now.AddHours(3), null, Now.AddMinutes(1));
        conversation.LastCustomerMessageAtUtc.ShouldBe(Now.AddMinutes(1));
        conversation.UnreadCount.ShouldBe(3);
    }

    [Fact]
    public void In_bot_mode_a_customer_message_asks_for_one_responder_turn()
    {
        var conversation = NewConversation();
        conversation.DequeueDomainEvents();

        var message = Receive(conversation);

        message.BotTurn.ShouldBe(BotTurnState.Pending);
        conversation.DomainEvents.ShouldHaveSingleItem().ShouldBe(new CustomerMessageReceived(conversation.Id, message.Id));
    }

    [Fact]
    public void In_human_mode_a_customer_message_asks_for_no_turn()
    {
        var conversation = NewConversation();
        conversation.TakeOver(Agent, Now).IsSuccess.ShouldBeTrue();
        conversation.DequeueDomainEvents();

        var message = Receive(conversation);

        message.BotTurn.ShouldBe(BotTurnState.None);
        conversation.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void The_bot_answers_with_free_form_messages_within_the_window_only()
    {
        var conversation = NewConversation();
        Receive(conversation, at: Now);

        var reply = conversation.ReplyAsBot(TextContent.Create("Bonjour ! Pour quelles dates ?").Value, Now.AddMinutes(1)).Value;

        reply.Direction.ShouldBe(MessageDirection.Outbound);
        reply.Author.ShouldBe(MessageAuthor.Bot);
        reply.Status.ShouldBe(MessageStatus.Queued);
        reply.AuthorUserId.ShouldBeNull();
        reply.DomainEvents.ShouldHaveSingleItem().ShouldBe(new OutboundMessageQueued(reply.Id, conversation.Id));
        conversation.LastActivityAtUtc.ShouldBe(Now.AddMinutes(1));

        conversation.ReplyAsBot(TextContent.Create("Toujours là ?").Value, Now.AddHours(24)).Error.ShouldBe(MessagingErrors.WindowClosed);
        conversation.ReplyAsBot(Template(), Now.AddMinutes(2)).Error.ShouldBe(MessagingErrors.ContentNotAllowed);
    }

    [Fact]
    public void A_conversation_in_human_mode_never_gets_a_bot_reply()
    {
        var conversation = NewConversation();
        Receive(conversation);
        conversation.TakeOver(Agent, Now);

        conversation.ReplyAsBot(TextContent.Create("Bonjour").Value, Now).Error.ShouldBe(MessagingErrors.BotSilenced);
    }

    [Fact]
    public void An_agent_answers_only_after_taking_the_conversation_over()
    {
        var conversation = NewConversation();
        Receive(conversation);

        conversation.ReplyAsAgent(TextContent.Create("Bonjour, ici Amel").Value, Agent, Now).Error.ShouldBe(MessagingErrors.NotTakenOver);

        conversation.TakeOver(Agent, Now).IsSuccess.ShouldBeTrue();
        var reply = conversation.ReplyAsAgent(TextContent.Create("Bonjour, ici Amel").Value, Agent, Now.AddMinutes(2)).Value;

        reply.Author.ShouldBe(MessageAuthor.Agent);
        reply.AuthorUserId.ShouldBe(Agent.UserId);
        reply.AuthorName.ShouldBe("agent@demo.local");
        conversation.UnreadCount.ShouldBe(0);
        conversation.LastReadAtUtc.ShouldBe(Now.AddMinutes(2));
    }

    [Fact]
    public void Only_the_agent_holding_the_conversation_answers_it()
    {
        var conversation = NewConversation();
        Receive(conversation);
        var colleague = new AuditActor(Guid.CreateVersion7(), "colleague@demo.local", null, null);
        conversation.HandOverToHuman("Réclamation", Now).IsSuccess.ShouldBeTrue();

        // Handed over by the bot, nobody took it yet.
        conversation.ReplyAsAgent(TextContent.Create("Bonjour").Value, Agent, Now).Error.ShouldBe(MessagingErrors.NotTakenOver);

        conversation.TakeOver(Agent, Now).IsSuccess.ShouldBeTrue();
        conversation.ReplyAsAgent(TextContent.Create("Bonjour").Value, colleague, Now).Error.ShouldBe(MessagingErrors.NotTakenOver);
        conversation.ReplyAsAgent(TextContent.Create("Bonjour").Value, AuditActor.System("system:tests"), Now).Error.ShouldBe(MessagingErrors.NotTakenOver);
        conversation.ReplyAsAgent(TextContent.Create("Bonjour").Value, Agent, Now).IsSuccess.ShouldBeTrue();

        conversation.TakeOver(colleague, Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();
        conversation.ReplyAsAgent(TextContent.Create("Re-bonjour").Value, Agent, Now.AddMinutes(2)).Error.ShouldBe(MessagingErrors.NotTakenOver);
        conversation.ReplyAsAgent(TextContent.Create("Re-bonjour").Value, colleague, Now.AddMinutes(2)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Outside_the_window_an_agent_sends_an_approved_template_only()
    {
        var conversation = NewConversation();
        Receive(conversation, at: Now.AddDays(-2));
        conversation.TakeOver(Agent, Now);

        conversation.ReplyAsAgent(TextContent.Create("Bonjour ?").Value, Agent, Now).Error.ShouldBe(MessagingErrors.WindowClosed);
        var template = conversation.ReplyAsAgent(Template(), Agent, Now).Value;

        template.Kind.ShouldBe(MessageKind.Template);
        template.Status.ShouldBe(MessageStatus.Queued);
    }

    [Fact]
    public void A_notification_is_a_template_sent_whatever_the_mode_and_the_window()
    {
        var conversation = NewConversation();
        conversation.TakeOver(Agent, Now);

        var notification = conversation.Notify(Template(), Now);

        notification.Author.ShouldBe(MessageAuthor.System);
        notification.Kind.ShouldBe(MessageKind.Template);
        notification.Status.ShouldBe(MessageStatus.Queued);
    }

    [Fact]
    public void Taking_over_records_who_and_when_and_handing_back_clears_it()
    {
        var conversation = NewConversation();
        conversation.DequeueDomainEvents();

        conversation.TakeOver(Agent, Now).IsSuccess.ShouldBeTrue();

        conversation.Mode.ShouldBe(ConversationMode.Human);
        conversation.HumanAgentId.ShouldBe(Agent.UserId);
        conversation.HumanAgentName.ShouldBe(Agent.UserName);
        conversation.HumanSinceUtc.ShouldBe(Now);
        conversation.DomainEvents.ShouldHaveSingleItem().ShouldBe(new ConversationModeChanged(conversation.Id, ConversationMode.Human, Agent.UserId));

        conversation.TakeOver(Agent, Now.AddMinutes(5)).IsSuccess.ShouldBeTrue();
        conversation.DomainEvents.Count.ShouldBe(1);

        conversation.HandBackToBot(Now.AddMinutes(10)).IsSuccess.ShouldBeTrue();
        conversation.Mode.ShouldBe(ConversationMode.Bot);
        conversation.HumanAgentId.ShouldBeNull();
        conversation.HumanAgentName.ShouldBeNull();
        conversation.HumanSinceUtc.ShouldBeNull();
        conversation.DomainEvents[^1].ShouldBe(new ConversationModeChanged(conversation.Id, ConversationMode.Bot, null));
        conversation.HandBackToBot(Now.AddMinutes(11)).IsSuccess.ShouldBeTrue();
        conversation.DomainEvents.Count.ShouldBe(2);
    }

    [Fact]
    public void Another_agent_can_take_a_conversation_over_from_a_colleague()
    {
        var conversation = NewConversation();
        conversation.TakeOver(Agent, Now);
        var colleague = new AuditActor(Guid.CreateVersion7(), "colleague@demo.local", null, null);

        conversation.TakeOver(colleague, Now.AddHours(1)).IsSuccess.ShouldBeTrue();

        conversation.HumanAgentId.ShouldBe(colleague.UserId);
        conversation.HumanSinceUtc.ShouldBe(Now);
    }

    [Fact]
    public void The_bot_hands_over_with_a_reason_and_keeps_quiet_until_an_agent_hands_back()
    {
        var conversation = NewConversation();
        Receive(conversation);

        conversation.HandOverToHuman("  Réclamation   sur une caution ", Now).IsSuccess.ShouldBeTrue();

        conversation.Mode.ShouldBe(ConversationMode.Human);
        conversation.HandoverReason.ShouldBe("Réclamation sur une caution");
        conversation.HumanAgentId.ShouldBeNull();
        conversation.HumanSinceUtc.ShouldBe(Now);
        conversation.ReplyAsBot(TextContent.Create("…").Value, Now).Error.ShouldBe(MessagingErrors.BotSilenced);
        conversation.HandOverToHuman("encore", Now).Error.ShouldBe(MessagingErrors.BotSilenced);
        conversation.HandOverToHuman(" ", Now).Error.ShouldBe(MessagingErrors.HandoverReasonInvalid);
        conversation.HandOverToHuman(new string('a', Conversation.HandoverReasonMaxLength + 1), Now).Error.ShouldBe(MessagingErrors.HandoverReasonInvalid);

        conversation.TakeOver(Agent, Now.AddMinutes(3));
        conversation.HandoverReason.ShouldBe("Réclamation sur une caution");
        conversation.HumanSinceUtc.ShouldBe(Now);
    }

    [Fact]
    public void Only_a_back_office_user_takes_a_conversation_over()
    {
        Should.Throw<ArgumentException>(() => NewConversation().TakeOver(AuditActor.System("system:tests"), Now));
    }

    [Fact]
    public void A_customer_message_reopens_a_closed_conversation_and_keeps_its_mode()
    {
        var conversation = NewConversation();
        Receive(conversation);
        conversation.TakeOver(Agent, Now);
        conversation.Close(Now.AddHours(1));
        conversation.Status.ShouldBe(ConversationStatus.Closed);
        conversation.ClosedAtUtc.ShouldBe(Now.AddHours(1));
        conversation.UnreadCount.ShouldBe(0);

        Receive(conversation, at: Now.AddDays(3));

        conversation.Status.ShouldBe(ConversationStatus.Open);
        conversation.ClosedAtUtc.ShouldBeNull();
        conversation.Mode.ShouldBe(ConversationMode.Human);
        conversation.UnreadCount.ShouldBe(1);
    }

    [Fact]
    public void Links_to_a_person_and_a_rental_file_can_be_set_and_removed()
    {
        var conversation = NewConversation();
        var personId = Guid.CreateVersion7();
        var customerId = Guid.CreateVersion7();
        var bookingId = Guid.CreateVersion7();

        conversation.LinkPerson(personId, customerId);
        conversation.LinkBooking(bookingId);

        conversation.PersonId.ShouldBe(personId);
        conversation.CustomerId.ShouldBe(customerId);
        conversation.BookingId.ShouldBe(bookingId);

        conversation.LinkPerson(null, customerId);
        conversation.LinkBooking(null);
        conversation.PersonId.ShouldBeNull();
        conversation.CustomerId.ShouldBeNull();
        conversation.BookingId.ShouldBeNull();
        Should.Throw<ArgumentException>(() => conversation.LinkBooking(Guid.Empty));
    }

    [Fact]
    public void Bot_turns_add_up_their_usage_and_the_need_is_replaced()
    {
        var conversation = NewConversation();

        conversation.RecordBotTurn(new BotTurnUsage(1200, 150, cacheReadTokens: 1000, cacheWriteTokens: 0, toolCalls: 1, aiCostUsd: 0.000195m, metaCostUsd: 0.0040m));
        conversation.RecordBotTurn(new BotTurnUsage(1300, 90, toolCalls: 2, aiCostUsd: 0.000175m));
        var need = ConversationNeed.Create("TUN", null, Now.AddDays(3), Now.AddDays(7), passengers: 4, luggage: 3, GearboxPreference.Automatic, Money.Tnd(600m)).Value;
        conversation.UpdateNeed(need);

        conversation.Usage.BotTurns.ShouldBe(2);
        conversation.Usage.InputTokens.ShouldBe(2500);
        conversation.Usage.OutputTokens.ShouldBe(240);
        conversation.Usage.CacheReadTokens.ShouldBe(1000);
        conversation.Usage.ToolCalls.ShouldBe(3);
        conversation.Usage.AiCostUsd.ShouldBe(0.00037m);
        conversation.Usage.MetaCostUsd.ShouldBe(0.004m);
        conversation.Need.ShouldBe(need);
        Should.Throw<ArgumentOutOfRangeException>(() => new BotTurnUsage(-1, 0));
    }

    private static TemplateContent Template() => TemplateContent.Create("conversation_follow_up", "fr", ["Hédi"]).Value;
}
