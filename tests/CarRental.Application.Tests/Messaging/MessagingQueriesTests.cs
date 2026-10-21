using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Tests.Messaging.Fakes;

namespace CarRental.Application.Tests.Messaging;

/// <summary>The read contract of the module for the other modules (and the AI agent of package 4B).</summary>
public sealed class MessagingQueriesTests : IAsyncDisposable
{
    private const string Customer = "21698765432";

    private readonly MessagingHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_conversation_is_found_by_id_number_and_person_with_its_messages_oldest_first()
    {
        var personId = _harness.AddPerson("+21698765432");
        _harness.Responder.Reply = _ => new ConversationReply { Message = new OutboundText("Bonjour !") };
        await _harness.CustomerWritesAsync(Customer, "Salam");
        var id = _harness.Conversations.ShouldHaveSingleItem().Id;

        var byId = await _harness.RunAsync<IMessagingQueries, ConversationInfo?>(q => q.GetConversationAsync(id, Ct));
        var byNumber = await _harness.RunAsync<IMessagingQueries, ConversationInfo?>(q => q.FindByNumberAsync("+21698765432", Ct));
        var byWaId = await _harness.RunAsync<IMessagingQueries, ConversationInfo?>(q => q.FindByNumberAsync(Customer, Ct));
        var ofPerson = await _harness.RunAsync<IMessagingQueries, IReadOnlyList<ConversationInfo>>(q => q.GetConversationsOfPersonAsync(personId, Ct));
        var messages = await _harness.RunAsync<IMessagingQueries, IReadOnlyList<ConversationMessageInfo>>(q => q.GetMessagesAsync(id, cancellationToken: Ct));
        var last = await _harness.RunAsync<IMessagingQueries, IReadOnlyList<ConversationMessageInfo>>(q => q.GetMessagesAsync(id, 1, Ct));

        byId.ShouldNotBeNull().WhatsAppNumber.ShouldBe("+21698765432");
        byId.Mode.ShouldBe(ConversationModeInfo.Bot);
        byId.IsOpen.ShouldBeTrue();
        byId.PersonId.ShouldBe(personId);
        byNumber.ShouldBe(byId);
        byWaId.ShouldBe(byId);
        ofPerson.ShouldHaveSingleItem().Id.ShouldBe(id);
        messages.Select(m => (m.Author, m.Text)).ShouldBe([(MessageAuthorInfo.Customer, "Salam"), (MessageAuthorInfo.Bot, "Bonjour !")]);
        last.ShouldHaveSingleItem().Author.ShouldBe(MessageAuthorInfo.Bot);
        (await _harness.RunAsync<IMessagingQueries, ConversationInfo?>(q => q.FindByNumberAsync("not a number", Ct))).ShouldBeNull();
    }
}
