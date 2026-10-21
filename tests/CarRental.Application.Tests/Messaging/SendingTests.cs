using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Sending;
using CarRental.Application.Messaging.Webhooks;
using CarRental.Application.Tests.Messaging.Fakes;
using CarRental.Domain.Messaging;

namespace CarRental.Application.Tests.Messaging;

/// <summary>
/// Sending through the outbox: one Graph API call per queued message, retried only when the message certainly did not
/// leave; refusals and unknown outcomes are final. Statuses reported by Meta update the message, once each.
/// </summary>
public sealed class SendingTests : IAsyncDisposable
{
    private const string Customer = "21650555014";

    private readonly MessagingHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task An_accepted_message_keeps_its_meta_id_and_is_never_sent_twice()
    {
        var queued = await BotReplyQueuedAsync();

        await _harness.HandleAsync(queued);
        await _harness.HandleAsync(queued, attempt: 2);

        _harness.WhatsApp.Sent.ShouldHaveSingleItem();
        var message = Reply(queued);
        message.Status.ShouldBe(MessageStatus.Accepted);
        message.MetaMessageId.ShouldBe("wamid.test1");
        message.AcceptedAtUtc.ShouldBe(_harness.Clock.GetUtcNow());
    }

    [Fact]
    public async Task A_refusal_of_meta_is_final_and_keeps_its_error()
    {
        var queued = await BotReplyQueuedAsync();
        _harness.WhatsApp.Answer(WhatsAppSendResult.Rejected(131026, "Message undeliverable"));

        await _harness.HandleAsync(queued);

        var message = Reply(queued);
        message.Status.ShouldBe(MessageStatus.Failed);
        message.FailureCode.ShouldBe(MessagingErrors.SendRejected.Code);
        message.MetaErrorCode.ShouldBe(131026);
        message.MetaErrorTitle.ShouldBe("Message undeliverable");
    }

    [Fact]
    public async Task A_transient_failure_is_retried_by_the_outbox_then_given_up()
    {
        var queued = await BotReplyQueuedAsync();
        _harness.WhatsApp.Answer(Enumerable.Repeat(WhatsAppSendResult.Transient(130429, "Rate limit hit"), OutboundMessageSender.MaxSendAttempts).ToArray());

        for (var attempt = 1; attempt < OutboundMessageSender.MaxSendAttempts; attempt++)
        {
            await Should.ThrowAsync<WhatsAppUnavailableException>(() => _harness.HandleAsync(queued, attempt));
            Reply(queued).Status.ShouldBe(MessageStatus.Queued);
        }

        await _harness.HandleAsync(queued, OutboundMessageSender.MaxSendAttempts);

        var message = Reply(queued);
        message.Status.ShouldBe(MessageStatus.Failed);
        message.FailureCode.ShouldBe(MessagingErrors.SendUnavailable.Code);
        message.MetaErrorCode.ShouldBe(130429);
        _harness.WhatsApp.Sent.Count.ShouldBe(OutboundMessageSender.MaxSendAttempts);
    }

    [Fact]
    public async Task An_unknown_outcome_is_never_retried_to_avoid_a_duplicate()
    {
        var queued = await BotReplyQueuedAsync();
        _harness.WhatsApp.Answer(WhatsAppSendResult.Unknown());

        await _harness.HandleAsync(queued);
        await _harness.HandleAsync(queued, attempt: 2);

        _harness.WhatsApp.Sent.ShouldHaveSingleItem();
        Reply(queued).FailureCode.ShouldBe(MessagingErrors.SendOutcomeUnknown.Code);
    }

    [Fact]
    public async Task A_free_form_message_is_not_sent_once_the_window_closed()
    {
        var queued = await BotReplyQueuedAsync();
        _harness.Clock.Advance(TimeSpan.FromHours(24));

        await _harness.HandleAsync(queued);

        _harness.WhatsApp.Sent.ShouldBeEmpty();
        var message = Reply(queued);
        message.Status.ShouldBe(MessageStatus.Cancelled);
        message.FailureCode.ShouldBe(MessagingErrors.WindowClosed.Code);
    }

    [Fact]
    public async Task Without_whatsapp_configured_nothing_leaves()
    {
        var queued = await BotReplyQueuedAsync();
        _harness.WhatsApp.IsEnabled = false;

        await _harness.HandleAsync(queued);

        _harness.WhatsApp.Sent.ShouldBeEmpty();
        Reply(queued).FailureCode.ShouldBe(MessagingErrors.Disabled.Code);
    }

    [Fact]
    public async Task Statuses_update_the_message_once_each_with_the_pricing_reported()
    {
        var queued = await BotReplyQueuedAsync();
        await _harness.HandleAsync(queued);
        var metaId = Reply(queued).MetaMessageId!;
        var at = _harness.Clock.GetUtcNow();
        var service = new MessagePricing("service", true, "PMP");

        await _harness.DeliverAsync(statuses:
        [
            new WhatsAppStatusReport(metaId, Customer, MessageStatus.Sent, at, null, null, service),
            new WhatsAppStatusReport(metaId, Customer, MessageStatus.Delivered, at.AddSeconds(2), null, null, service),
        ]);
        await _harness.DeliverAsync(statuses:
        [
            new WhatsAppStatusReport(metaId, Customer, MessageStatus.Delivered, at.AddSeconds(2), null, null, service),
            new WhatsAppStatusReport(metaId, Customer, MessageStatus.Read, at.AddMinutes(1), null, null, null),
            new WhatsAppStatusReport("wamid.not-ours", Customer, MessageStatus.Read, at, null, null, null),
        ]);

        var message = Reply(queued);
        message.Status.ShouldBe(MessageStatus.Read);
        message.StatusChanges.Select(c => c.Status).ShouldBe([MessageStatus.Sent, MessageStatus.Delivered, MessageStatus.Read]);
        message.PricingCategory.ShouldBe("service");
        message.IsBillable.ShouldBe(true);
        message.ReadAtUtc.ShouldBe(at.AddMinutes(1));
    }

    [Fact]
    public async Task A_failed_status_marks_the_message_failed_with_the_error_of_meta()
    {
        var queued = await BotReplyQueuedAsync();
        await _harness.HandleAsync(queued);
        var metaId = Reply(queued).MetaMessageId!;

        await _harness.DeliverAsync(statuses:
            [new WhatsAppStatusReport(metaId, Customer, MessageStatus.Failed, _harness.Clock.GetUtcNow(), 131047, "Re-engagement message", null)]);

        var message = Reply(queued);
        message.Status.ShouldBe(MessageStatus.Failed);
        message.MetaErrorCode.ShouldBe(131047);
        message.MetaErrorTitle.ShouldBe("Re-engagement message");
    }

    /// <summary>A customer writes, the bot answers: the reply is queued (the send itself is left to the test).</summary>
    private async Task<OutboundMessageQueued> BotReplyQueuedAsync()
    {
        _harness.Responder.Reply = _ => new ConversationReply { Message = new OutboundText("Bonjour ! Pour quelles dates ?") };
        var payload = $"payload-{Guid.NewGuid():N}";
        _harness.Parser.Batches[payload] = new WhatsAppWebhookBatch(
            [new InboundWhatsAppMessage($"wamid.{Guid.NewGuid():N}", Customer, "Rania", _harness.Clock.GetUtcNow(), TextContent.Received("Bonjour"))],
            [],
            0);
        await _harness.HandleAsync(new WhatsAppWebhookReceived(payload, _harness.Clock.GetUtcNow()));
        var turn = _harness.Db.PublishedEvents.OfType<CustomerMessageReceived>().Single();
        _harness.Db.PublishedEvents.Clear();
        await _harness.HandleAsync(turn);
        var queued = _harness.Db.PublishedEvents.OfType<OutboundMessageQueued>().Single();
        _harness.Db.PublishedEvents.Clear();
        return queued;
    }

    private Message Reply(OutboundMessageQueued queued) => _harness.Messages.Single(m => m.Id == queued.MessageId);
}
