using CarRental.Domain.Messaging;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using static CarRental.Domain.Tests.Messaging.ConversationTests;

namespace CarRental.Domain.Tests.Messaging;

public sealed class MessageTests
{
    private static readonly MessagePricing Service = new("service", Billable: true, Model: "PMP");

    [Fact]
    public void An_accepted_message_follows_the_statuses_reported_by_meta()
    {
        var message = AcceptedReply();
        message.Status.ShouldBe(MessageStatus.Accepted);
        message.MetaMessageId.ShouldBe("wamid.reply");
        message.AcceptedAtUtc.ShouldBe(Now.AddSeconds(1));

        message.RecordStatus(MessageStatus.Sent, Now.AddSeconds(2), null, null, Service, Now.AddSeconds(3)).ShouldBeTrue();
        message.RecordStatus(MessageStatus.Delivered, Now.AddSeconds(4), null, null, Service, Now.AddSeconds(5)).ShouldBeTrue();
        message.RecordStatus(MessageStatus.Read, Now.AddMinutes(1), null, null, null, Now.AddMinutes(1)).ShouldBeTrue();

        message.Status.ShouldBe(MessageStatus.Read);
        message.SentAtUtc.ShouldBe(Now.AddSeconds(2));
        message.DeliveredAtUtc.ShouldBe(Now.AddSeconds(4));
        message.ReadAtUtc.ShouldBe(Now.AddMinutes(1));
        message.PricingCategory.ShouldBe("service");
        message.IsBillable.ShouldBe(true);
        message.PricingModel.ShouldBe("PMP");
        message.StatusChanges.Select(c => c.Status).ShouldBe([MessageStatus.Sent, MessageStatus.Delivered, MessageStatus.Read]);
        message.StatusChanges.ShouldAllBe(c => c.MessageId == message.Id && c.ErrorCode == null);
    }

    [Fact]
    public void A_duplicate_status_is_ignored()
    {
        var message = AcceptedReply();
        message.RecordStatus(MessageStatus.Delivered, Now, null, null, Service, Now).ShouldBeTrue();

        message.RecordStatus(MessageStatus.Delivered, Now.AddMinutes(5), null, null, null, Now.AddMinutes(5)).ShouldBeFalse();

        message.StatusChanges.ShouldHaveSingleItem();
        message.DeliveredAtUtc.ShouldBe(Now);
    }

    [Fact]
    public void Statuses_arriving_out_of_order_keep_the_most_advanced_one()
    {
        var message = AcceptedReply();

        message.RecordStatus(MessageStatus.Read, Now.AddMinutes(2), null, null, null, Now.AddMinutes(2));
        message.RecordStatus(MessageStatus.Delivered, Now.AddMinutes(1), null, null, null, Now.AddMinutes(3));
        message.RecordStatus(MessageStatus.Sent, Now, null, null, null, Now.AddMinutes(4));

        message.Status.ShouldBe(MessageStatus.Read);
        message.StatusChanges.Count.ShouldBe(3);
    }

    [Fact]
    public void A_failed_status_keeps_the_error_reported_by_meta()
    {
        var message = AcceptedReply();
        message.RecordStatus(MessageStatus.Sent, Now, null, null, null, Now);

        message.RecordStatus(MessageStatus.Failed, Now.AddSeconds(5), 131026, "Message undeliverable", null, Now.AddSeconds(6)).ShouldBeTrue();

        message.Status.ShouldBe(MessageStatus.Failed);
        message.FailedAtUtc.ShouldBe(Now.AddSeconds(5));
        message.MetaErrorCode.ShouldBe(131026);
        message.MetaErrorTitle.ShouldBe("Message undeliverable");
        message.FailureCode.ShouldBe(MessagingErrors.SendRejected.Code);
        message.StatusChanges[^1].ErrorCode.ShouldBe(131026);
        message.StatusChanges[^1].ErrorTitle.ShouldBe("Message undeliverable");
    }

    [Fact]
    public void A_message_delivered_after_a_failure_is_delivered()
    {
        var message = AcceptedReply();
        message.RecordStatus(MessageStatus.Failed, Now, 131000, "Something went wrong", null, Now);

        message.RecordStatus(MessageStatus.Delivered, Now.AddMinutes(1), null, null, null, Now.AddMinutes(1));

        message.Status.ShouldBe(MessageStatus.Delivered);
    }

    [Fact]
    public void Statuses_never_apply_to_inbound_queued_or_cancelled_messages()
    {
        var conversation = NewConversation();
        var inbound = Receive(conversation);
        var queued = conversation.ReplyAsBot(TextContent.Create("Bonjour").Value, Now).Value;
        var cancelled = conversation.ReplyAsBot(TextContent.Create("Encore").Value, Now).Value;
        cancelled.Cancel(MessagingErrors.BotSilenced, Now);

        inbound.RecordStatus(MessageStatus.Read, Now, null, null, null, Now).ShouldBeFalse();
        queued.RecordStatus(MessageStatus.Sent, Now, null, null, null, Now).ShouldBeFalse();
        cancelled.RecordStatus(MessageStatus.Sent, Now, null, null, null, Now).ShouldBeFalse();
        inbound.Status.ShouldBe(MessageStatus.Received);
        Should.Throw<ArgumentOutOfRangeException>(() => queued.RecordStatus(MessageStatus.Accepted, Now, null, null, null, Now));
    }

    [Fact]
    public void A_queued_message_fails_or_is_cancelled_with_its_reason()
    {
        var conversation = NewConversation();
        Receive(conversation);
        var refused = conversation.ReplyAsBot(TextContent.Create("Bonjour").Value, Now).Value;
        var silenced = conversation.ReplyAsBot(TextContent.Create("Bonjour").Value, Now).Value;

        refused.MarkNotSent(MessagingErrors.SendRejected, 131047, "Re-engagement message", Now);
        silenced.Cancel(MessagingErrors.BotSilenced, Now);

        refused.Status.ShouldBe(MessageStatus.Failed);
        refused.FailureCode.ShouldBe("messaging.send.rejected");
        refused.MetaErrorCode.ShouldBe(131047);
        silenced.Status.ShouldBe(MessageStatus.Cancelled);
        silenced.FailureCode.ShouldBe("messaging.conversation.bot_silenced");
        Should.Throw<InvalidOperationException>(() => refused.MarkAccepted("wamid.late", Now));
        Should.Throw<InvalidOperationException>(() => silenced.Cancel(MessagingErrors.BotSilenced, Now));
    }

    [Fact]
    public void A_message_is_accepted_only_while_being_sent_and_goes_back_to_the_queue_after_a_transient_failure()
    {
        var conversation = NewConversation();
        Receive(conversation);
        var reply = conversation.ReplyAsBot(TextContent.Create("Bonjour").Value, Now).Value;

        Should.Throw<InvalidOperationException>(() => reply.MarkAccepted("wamid.too-early", Now));
        Should.Throw<InvalidOperationException>(() => reply.ReturnToQueue());
        reply.MarkSending();
        reply.Status.ShouldBe(MessageStatus.Sending);
        reply.RecordStatus(MessageStatus.Sent, Now, null, null, null, Now).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => reply.MarkSending());
        Should.Throw<InvalidOperationException>(() => reply.Cancel(MessagingErrors.BotSilenced, Now));

        reply.ReturnToQueue();
        reply.Status.ShouldBe(MessageStatus.Queued);
        reply.MarkSending();
        reply.MarkAccepted(" wamid.ok ", Now.AddSeconds(1));

        reply.Status.ShouldBe(MessageStatus.Accepted);
        reply.MetaMessageId.ShouldBe("wamid.ok");
    }

    [Fact]
    public void A_message_being_sent_whose_outcome_is_lost_fails_as_unknown()
    {
        var conversation = NewConversation();
        Receive(conversation);
        var reply = conversation.ReplyAsBot(TextContent.Create("Bonjour").Value, Now).Value;
        reply.MarkSending();

        reply.MarkNotSent(MessagingErrors.SendOutcomeUnknown, null, null, Now.AddMinutes(1));

        reply.Status.ShouldBe(MessageStatus.Failed);
        reply.FailureCode.ShouldBe("messaging.send.outcome_unknown");
        reply.FailedAtUtc.ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public void A_bot_turn_is_completed_once()
    {
        var conversation = NewConversation();
        var message = Receive(conversation);

        message.CompleteBotTurn(BotTurnState.Replied).ShouldBeTrue();
        message.CompleteBotTurn(BotTurnState.Superseded).ShouldBeFalse();

        message.BotTurn.ShouldBe(BotTurnState.Replied);
        Should.Throw<ArgumentOutOfRangeException>(() => message.CompleteBotTurn(BotTurnState.Pending));
    }

    [Fact]
    public void An_inbound_message_keeps_only_the_kind_and_the_meta_id_of_a_media()
    {
        var conversation = NewConversation();

        var message = conversation.ReceiveCustomerMessage("wamid.photo", MediaContent.Received(MediaKind.Image, "1234567890"), Now, null, Now);

        message.Kind.ShouldBe(MessageKind.Media);
        message.Text.ShouldBeNull();
        var media = message.Content.ShouldBeOfType<MediaContent>();
        media.MediaKind.ShouldBe(MediaKind.Image);
        media.MediaId.ShouldBe("1234567890");
        message.Content.ToString().ShouldBe("Media");
    }

    [Property(MaxTest = 500)]
    public Property The_final_status_does_not_depend_on_the_order_of_the_webhooks()
    {
        var statuses = new[] { MessageStatus.Sent, MessageStatus.Delivered, MessageStatus.Read, MessageStatus.Failed };
        var arrivals = Gen.Shuffle(statuses)
            .SelectMany(order => Gen.Choose(1, order.Length).Select(count => order.Take(count).ToArray()))
            .ToArbitrary();
        return Prop.ForAll(arrivals, received =>
        {
            var message = AcceptedReply();
            foreach (var status in received.Concat(received))
            {
                message.RecordStatus(status, Now, status == MessageStatus.Failed ? 131000 : null, null, null, Now);
            }

            var expected = received.Contains(MessageStatus.Read) ? MessageStatus.Read
                : received.Contains(MessageStatus.Delivered) ? MessageStatus.Delivered
                : received.Contains(MessageStatus.Failed) ? MessageStatus.Failed
                : MessageStatus.Sent;
            return message.Status == expected && message.StatusChanges.Count == received.Length;
        });
    }

    private static Message AcceptedReply()
    {
        var conversation = NewConversation();
        Receive(conversation);
        var reply = conversation.ReplyAsBot(TextContent.Create("Bonjour ! Pour quelles dates ?").Value, Now).Value;
        reply.MarkSending();
        reply.MarkAccepted("wamid.reply", Now.AddSeconds(1));
        return reply;
    }
}
