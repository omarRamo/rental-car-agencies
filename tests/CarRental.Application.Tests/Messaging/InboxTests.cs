using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Inbox;
using CarRental.Application.Tests.Messaging.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;

namespace CarRental.Application.Tests.Messaging;

/// <summary>
/// The shared inbox: take over (audited) and hand back, answer within the window, a template outside it, link a person or
/// a rental file, read state — each behind <c>whatsapp.inbox</c>.
/// </summary>
public sealed class InboxTests : IAsyncDisposable
{
    private const string Customer = "21655444004";

    private readonly MessagingHarness _harness = new();
    private readonly UserContextSnapshot _agent = MessagingHarness.Agent();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task An_agent_takes_over_answers_and_hands_back_with_audit()
    {
        var conversationId = await ConversationAsync();

        (await Inbox(s => s.TakeOverAsync(conversationId, TestContext.Current.CancellationToken))).IsSuccess.ShouldBeTrue();
        (await Inbox(s => s.SendTextAsync(conversationId, "Bonjour, ici Amel de l'agence.", TestContext.Current.CancellationToken))).IsSuccess.ShouldBeTrue();
        await _harness.DrainAsync();
        (await Inbox(s => s.HandBackToBotAsync(conversationId, TestContext.Current.CancellationToken))).IsSuccess.ShouldBeTrue();

        _harness.WhatsApp.Sent.ShouldHaveSingleItem().Content.ShouldBeOfType<TextContent>().Body.ShouldBe("Bonjour, ici Amel de l'agence.");
        var reply = _harness.Messages.Single(m => m.Author == MessageAuthor.Agent);
        reply.AuthorUserId.ShouldBe(_agent.UserId);
        reply.Status.ShouldBe(MessageStatus.Accepted);
        var conversation = _harness.Conversations.ShouldHaveSingleItem();
        conversation.Mode.ShouldBe(ConversationMode.Bot);
        conversation.UnreadCount.ShouldBe(0);
        _harness.Audit.Events.Select(e => e.Action).ShouldBe(
        [
            MessagingAuditActions.ConversationTakenOver,
            MessagingAuditActions.MessageSentByAgent,
            MessagingAuditActions.ConversationHandedBack,
        ]);
        _harness.Audit.Events.ShouldAllBe(e => e.EntityId == conversationId.ToString() && !e.Details!.Contains("Amel"));
    }

    [Fact]
    public async Task An_agent_answers_only_after_taking_over()
    {
        var conversationId = await ConversationAsync();

        (await Inbox(s => s.SendTextAsync(conversationId, "Bonjour", TestContext.Current.CancellationToken))).Error.ShouldBe(MessagingErrors.NotTakenOver);
        _harness.WhatsApp.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Outside_the_window_the_agent_sends_an_approved_template()
    {
        var conversationId = await ConversationAsync();
        await Inbox(s => s.TakeOverAsync(conversationId, TestContext.Current.CancellationToken));
        _harness.Clock.Advance(TimeSpan.FromHours(30));

        (await Inbox(s => s.SendTextAsync(conversationId, "Toujours intéressé ?", TestContext.Current.CancellationToken))).Error.ShouldBe(MessagingErrors.WindowClosed);
        (await Inbox(s => s.SendTemplateAsync(conversationId, InboxTemplateKind.FollowUp, [], TestContext.Current.CancellationToken))).Error
            .ShouldBe(MessagingErrors.TemplateParameterInvalid);
        (await Inbox(s => s.SendTemplateAsync(conversationId, InboxTemplateKind.FollowUp, ["Nour"], TestContext.Current.CancellationToken))).IsSuccess.ShouldBeTrue();
        await _harness.DrainAsync();

        var template = _harness.WhatsApp.Sent.ShouldHaveSingleItem().Content.ShouldBeOfType<TemplateContent>();
        template.Name.ShouldBe("conversation_follow_up");
        template.Parameters.ShouldBe(["Nour"]);
    }

    [Fact]
    public async Task A_template_without_a_configured_name_cannot_be_sent()
    {
        var conversationId = await ConversationAsync();
        await Inbox(s => s.TakeOverAsync(conversationId, TestContext.Current.CancellationToken));
        _harness.Settings.Set(MessagingSettings.FollowUpTemplate, string.Empty);

        (await Inbox(s => s.SendTemplateAsync(conversationId, InboxTemplateKind.FollowUp, ["Nour"], TestContext.Current.CancellationToken))).Error
            .ShouldBe(MessagingErrors.TemplateNotConfigured);
        var templates = (await _harness.RunAsync<WhatsAppInboxQueries, Result<IReadOnlyList<InboxTemplate>>>(q => q.GetTemplatesAsync(TestContext.Current.CancellationToken), _agent)).Value;
        templates.ShouldNotContain(t => t.Kind == InboxTemplateKind.FollowUp);
        templates.Single(t => t.Kind == InboxTemplateKind.PickupReminder).ParameterCount.ShouldBe(5);
    }

    [Fact]
    public async Task Without_the_inbox_permission_nothing_is_read_nor_changed()
    {
        var conversationId = await ConversationAsync();
        var accountant = UserContextSnapshot.ForUser(Guid.CreateVersion7(), "comptable@demo.local", RolePresets.PermissionsOf(Roles.Accountant));

        (await Inbox(s => s.TakeOverAsync(conversationId, TestContext.Current.CancellationToken), accountant)).Error.ShouldBe(MessagingErrors.Forbidden);
        (await _harness.RunAsync<WhatsAppInboxQueries, Result<PagedResult<InboxConversationItem>>>(
            q => q.ListAsync(new InboxFilter(), new PageRequest(), TestContext.Current.CancellationToken),
            accountant)).Error.ShouldBe(MessagingErrors.Forbidden);
        (await _harness.RunAsync<WhatsAppInboxQueries, Result<InboxConversationDetails>>(q => q.GetAsync(conversationId, TestContext.Current.CancellationToken), accountant))
            .Error.ShouldBe(MessagingErrors.Forbidden);
    }

    [Fact]
    public async Task Linking_needs_a_known_person_and_an_existing_file()
    {
        var conversationId = await ConversationAsync();
        var personId = _harness.AddPerson("+21699999999");
        var booking = _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Confirmed, _harness.Clock.GetUtcNow().AddDays(3), _harness.Clock.GetUtcNow().AddDays(5), personId);

        (await Inbox(s => s.LinkPersonAsync(conversationId, Guid.CreateVersion7(), TestContext.Current.CancellationToken))).Error.ShouldBe(MessagingErrors.PersonNotFound);
        (await Inbox(s => s.LinkBookingAsync(conversationId, "LOC-2027-999999", TestContext.Current.CancellationToken))).Error.ShouldBe(MessagingErrors.BookingNotFound);
        (await Inbox(s => s.LinkPersonAsync(conversationId, personId, TestContext.Current.CancellationToken))).IsSuccess.ShouldBeTrue();
        (await Inbox(s => s.LinkBookingAsync(conversationId, $" {booking.Number.ToLowerInvariant()} ", TestContext.Current.CancellationToken))).IsSuccess.ShouldBeTrue();

        var details = (await _harness.RunAsync<WhatsAppInboxQueries, Result<InboxConversationDetails>>(q => q.GetAsync(conversationId, TestContext.Current.CancellationToken), _agent)).Value;
        details.Person!.Id.ShouldBe(personId);
        details.Person.FullName.ShouldBe("Hédi Ben Salah");
        details.Booking!.Number.ShouldBe(booking.Number);
        _harness.Audit.Events.Count(e => e.Action == MessagingAuditActions.ConversationLinked).ShouldBe(2);

        (await Inbox(s => s.LinkBookingAsync(conversationId, null, TestContext.Current.CancellationToken))).IsSuccess.ShouldBeTrue();
        _harness.Conversations[0].BookingId.ShouldBeNull();
    }

    [Fact]
    public async Task Linking_a_person_requires_reading_customers()
    {
        var conversationId = await ConversationAsync();
        var inboxOnly = UserContextSnapshot.ForUser(Guid.CreateVersion7(), "extra@demo.local", [Permissions.WhatsAppInbox]);

        (await Inbox(s => s.LinkPersonAsync(conversationId, _harness.AddPerson("+21699999998"), TestContext.Current.CancellationToken), inboxOnly)).Error
            .ShouldBe(MessagingErrors.Forbidden);
    }

    [Fact]
    public async Task The_list_shows_mode_unread_window_and_last_message_with_filters()
    {
        var botConversation = await ConversationAsync(Customer, "Bonjour, avez-vous une Clio ?");
        var humanConversation = await ConversationAsync("21655444005", "Je veux parler à un agent");
        await Inbox(s => s.TakeOverAsync(humanConversation, TestContext.Current.CancellationToken));
        await Inbox(s => s.MarkReadAsync(humanConversation, TestContext.Current.CancellationToken));

        var all = await ListAsync(new InboxFilter());
        var human = await ListAsync(new InboxFilter(InboxView.Human));
        var unread = await ListAsync(new InboxFilter(InboxView.Unread));
        var searched = await ListAsync(new InboxFilter(Search: "444005"));

        all.TotalCount.ShouldBe(2);
        var item = all.Items.Single(i => i.Id == botConversation);
        item.Mode.ShouldBe(ConversationModeInfo.Bot);
        item.UnreadCount.ShouldBe(1);
        item.WindowClosesAtUtc.ShouldBe(_harness.Messages.Single(m => m.ConversationId == botConversation).OccurredAtUtc.AddHours(24));
        item.LastMessage!.Text.ShouldBe("Bonjour, avez-vous une Clio ?");
        item.LastMessage.Direction.ShouldBe(MessageDirectionInfo.Inbound);
        human.Items.ShouldHaveSingleItem().Id.ShouldBe(humanConversation);
        human.Items[0].HumanAgentName.ShouldBe("agent@demo.local");
        unread.Items.ShouldHaveSingleItem().Id.ShouldBe(botConversation);
        searched.Items.ShouldHaveSingleItem().Id.ShouldBe(humanConversation);
    }

    [Fact]
    public async Task Agents_are_shown_by_their_display_name()
    {
        var conversationId = await ConversationAsync();
        _harness.Identity.Names[_agent.UserId!.Value] = "Amel Trabelsi";
        await Inbox(s => s.TakeOverAsync(conversationId, TestContext.Current.CancellationToken));
        await Inbox(s => s.SendTextAsync(conversationId, "Bonjour", TestContext.Current.CancellationToken));

        var item = (await ListAsync(new InboxFilter(InboxView.Human))).Items.ShouldHaveSingleItem();
        var details = (await _harness.RunAsync<WhatsAppInboxQueries, Result<InboxConversationDetails>>(
            q => q.GetAsync(conversationId, TestContext.Current.CancellationToken),
            _agent)).Value;

        item.HumanAgentName.ShouldBe("Amel Trabelsi");
        details.HumanAgentName.ShouldBe("Amel Trabelsi");
        details.Messages.Single(m => m.Author == MessageAuthorInfo.Agent).AuthorName.ShouldBe("Amel Trabelsi");
    }

    [Fact]
    public async Task Closing_hides_the_conversation_until_the_customer_writes_again()
    {
        var conversationId = await ConversationAsync();

        (await Inbox(s => s.CloseAsync(conversationId, TestContext.Current.CancellationToken))).IsSuccess.ShouldBeTrue();
        (await ListAsync(new InboxFilter())).Items.ShouldBeEmpty();
        (await ListAsync(new InboxFilter(InboxView.Closed))).Items.ShouldHaveSingleItem();

        await _harness.CustomerWritesAsync(Customer, "Re-bonjour");
        (await ListAsync(new InboxFilter())).Items.ShouldHaveSingleItem().Id.ShouldBe(conversationId);
    }

    [Fact]
    public async Task The_thread_shows_the_messages_oldest_first_with_their_delivery()
    {
        _harness.Responder.Reply = _ => new ConversationReply
        {
            Message = new OutboundList("Où prenez-vous la voiture ?", "Choisir", [new ListSectionInfo(null, [new ListRowInfo("TUN", "Aéroport Tunis")])]),
        };
        var conversationId = await ConversationAsync();

        var details = (await _harness.RunAsync<WhatsAppInboxQueries, Result<InboxConversationDetails>>(q => q.GetAsync(conversationId, TestContext.Current.CancellationToken), _agent)).Value;

        details.Messages.Select(m => m.Author).ShouldBe([MessageAuthorInfo.Customer, MessageAuthorInfo.Bot]);
        var list = details.Messages[1];
        list.Kind.ShouldBe(MessageKindInfo.Interactive);
        list.Choices.ShouldBe(["Aéroport Tunis"]);
        list.ListButton.ShouldBe("Choisir");
        list.Status.ShouldBe(MessageStatusInfo.Accepted);
        details.IsWindowOpen.ShouldBeTrue();
        details.HasEarlierMessages.ShouldBeFalse();
    }

    private async Task<Guid> ConversationAsync(string from = Customer, string text = "Bonjour")
    {
        await _harness.CustomerWritesAsync(from, text);
        return _harness.Conversations.Single(c => c.WhatsAppNumber == "+" + from).Id;
    }

    private Task<Result> Inbox(Func<WhatsAppInboxService, Task<Result>> action, UserContextSnapshot? actor = null) =>
        _harness.RunAsync(action, actor ?? _agent);

    private async Task<PagedResult<InboxConversationItem>> ListAsync(InboxFilter filter) =>
        (await _harness.RunAsync<WhatsAppInboxQueries, Result<PagedResult<InboxConversationItem>>>(q => q.ListAsync(filter, new PageRequest(), TestContext.Current.CancellationToken), _agent)).Value;
}
