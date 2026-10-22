using System.Net;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Messaging.Agent.Knowledge;
using CarRental.Application.Messaging.Inbox;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using CarRental.Domain.Messaging.Agent;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Support;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Messaging.WhatsAppTestData;

namespace CarRental.Integration.Tests.Messaging;

/// <summary>
/// The WhatsApp data of each agency stays its own (ADR-0067), on SQL Server: the platform's number serves the configured
/// agency, the inbox and the knowledge base of an agency never show nor change those of another (the same number or
/// topic may exist in both), and the WhatsApp screens are refused while the module of the agency is off.
/// </summary>
public sealed class WhatsAppTenancyTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_message_to_the_number_belongs_to_the_configured_agency_even_when_another_has_the_module_on()
    {
        var other = await NewAgencyAsync("Routage");
        await TestModules.EnableAsync(host.Factory.Services, other, AgencyModules.WhatsApp, Ct);
        var waId = NewWaId();
        using var client = host.CreateClient();

        (await PostAsync(client, Text(waId, "Bonjour, une voiture pour samedi ?"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var conversation = await WaitForAsync(() => ConversationAsync(host, waId, Ct), c => c is not null, "the conversation", Ct);
        conversation!.AgencyId.ShouldBe(host.DemoAgencyId, "WhatsApp:AgencySlug designates the demo agency");
        (await MessagesAsync(host, conversation.Id, Ct)).ShouldAllBe(m => m.AgencyId == host.DemoAgencyId);
    }

    [Fact]
    public async Task The_inbox_of_an_agency_never_shows_nor_changes_the_conversations_of_another()
    {
        var waId = NewWaId();
        using var client = host.CreateClient();
        (await PostAsync(client, Text(waId, "Bonjour de Tunis", "Leila"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var served = await WaitForAsync(() => ConversationAsync(host, waId, Ct), c => c is not null, "the conversation", Ct);

        // The same customer number in another agency (its WhatsApp module off: no bot turn runs for it).
        var other = await NewAgencyAsync("Boîte");
        var theirs = await ConversationOfAsync(other, "+" + waId, "Bonjour de Sousse");
        var (ours, their) = (Agent(host), TestActors.Staff(other, Roles.Agent));
        var search = new InboxFilter(View: InboxView.All, Search: waId[^7..]);

        var ourList = await RunAsync<WhatsAppInboxQueries, Result<PagedResult<InboxConversationItem>>>(ours, q => q.ListAsync(search, new PageRequest(), Ct));
        var theirList = await RunAsync<WhatsAppInboxQueries, Result<PagedResult<InboxConversationItem>>>(their, q => q.ListAsync(search, new PageRequest(), Ct));
        var ourRead = await RunAsync<WhatsAppInboxQueries, Result<InboxConversationDetails>>(ours, q => q.GetAsync(theirs, Ct));
        var theirRead = await RunAsync<WhatsAppInboxQueries, Result<InboxConversationDetails>>(their, q => q.GetAsync(served!.Id, Ct));
        var takenOver = await RunAsync<WhatsAppInboxService, Result>(their, s => s.TakeOverAsync(served!.Id, Ct));
        var replied = await RunAsync<WhatsAppInboxService, Result>(their, s => s.SendTextAsync(served!.Id, "Réponse d'une autre agence", Ct));

        ourList.Value.Items.ShouldHaveSingleItem().Id.ShouldBe(served!.Id);
        theirList.Value.Items.ShouldHaveSingleItem().Id.ShouldBe(theirs);
        ourRead.Error.ShouldBe(MessagingErrors.ConversationNotFound);
        theirRead.Error.ShouldBe(MessagingErrors.ConversationNotFound);
        takenOver.Error.ShouldBe(MessagingErrors.ConversationNotFound);
        replied.Error.ShouldBe(MessagingErrors.ConversationNotFound);
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        (await db.Set<Conversation>().AsNoTracking().SingleAsync(c => c.Id == served.Id, Ct)).HumanAgentId.ShouldBeNull("the other agency took nothing over");
        (await db.Set<Message>().AnyAsync(m => m.ConversationId == served.Id && m.Text == "Réponse d'une autre agence", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Each_agency_has_its_own_knowledge_base_with_the_same_topics_allowed()
    {
        var other = await NewAgencyAsync("Savoir");
        var topic = "k" + NewName().ToLowerInvariant();
        var (ours, their) = (TestActors.Staff(host.DemoAgencyId, Roles.Manager), TestActors.Staff(other, Roles.Manager));

        var ourEntry = await RunAsync<KnowledgeBaseService, Result<Guid>>(ours, k => k.CreateAsync(Form(topic, "Parking à Tunis"), Ct));
        var theirEntry = await RunAsync<KnowledgeBaseService, Result<Guid>>(their, k => k.CreateAsync(Form(topic, "Parking à Sousse"), Ct));
        var twin = await RunAsync<KnowledgeBaseService, Result<Guid>>(their, k => k.CreateAsync(Form(topic, "Doublon"), Ct));
        var theirList = await RunAsync<KnowledgeBaseQueries, Result<PagedResult<KnowledgeEntryItem>>>(their, k => k.ListAsync(new PageRequest(1, PageRequest.MaxPageSize), Ct));
        var crossValidated = await RunAsync<KnowledgeBaseService, Result>(their, k => k.ValidateAsync(ourEntry.Value, Ct));
        var crossEdited = await RunAsync<KnowledgeBaseService, Result>(their, k => k.UpdateAsync(ourEntry.Value, Form(topic, "Réécrit"), Ct));

        ourEntry.IsSuccess.ShouldBeTrue(ourEntry.IsFailure ? ourEntry.Error.ToString() : null);
        theirEntry.IsSuccess.ShouldBeTrue("a topic is unique within an agency only");
        twin.Error.ShouldBe(KnowledgeErrors.TopicTaken);
        theirList.Value.Items.ShouldContain(e => e.Id == theirEntry.Value);
        theirList.Value.Items.ShouldNotContain(e => e.Id == ourEntry.Value);
        crossValidated.Error.ShouldBe(KnowledgeErrors.NotFound);
        crossEdited.Error.ShouldBe(KnowledgeErrors.NotFound);
        await using var scope = host.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<KnowledgeEntry>().AsNoTracking().SingleAsync(e => e.Id == ourEntry.Value, Ct);
        stored.AgencyId.ShouldBe(host.DemoAgencyId);
        stored.Title.ShouldBe("Parking à Tunis");
        stored.IsValidated.ShouldBeFalse();
    }

    [Fact]
    public async Task The_whatsapp_screens_are_refused_while_the_module_of_the_agency_is_off()
    {
        var agency = await NewAgencyAsync("Écrans");
        var (_, email) = await TestStaff.CreateAsync(host, Roles.Manager, agencyId: agency);
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, email, "/admin", Ct);
        var inbox = new Uri("/admin/whatsapp", UriKind.Relative);
        var knowledge = new Uri("/admin/whatsapp/knowledge", UriKind.Relative);

        var refused = await client.GetAsync(inbox, Ct);
        var knowledgeRefused = await client.GetAsync(knowledge, Ct);
        await TestModules.EnableAsync(host.Factory.Services, agency, AgencyModules.WhatsApp, Ct);
        var allowed = await client.GetAsync(inbox, Ct);

        await ShouldBeDeniedAsync(refused);
        await ShouldBeDeniedAsync(knowledgeRefused);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, "same session: the module applies at the next request");
    }

    private static KnowledgeEntryForm Form(string topic, string title) =>
        new(topic, title, "parking, stationnement", $"{title} : réponse de l'agence.", $"{title}: the agency's answer.", null);

    private static async Task ShouldBeDeniedAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync(Ct));
        HttpSignIn.PathAndQuery(response.Headers.Location!).ShouldContain("/account/access-denied");
    }

    private async Task<T> RunAsync<TService, T>(UserContextSnapshot actor, Func<TService, Task<T>> call)
        where TService : notnull
    {
        await using var scope = host.CreateScope(actor);
        return await call(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>A new agency of the test (its optional modules off, as for any new agency), with its office.</summary>
    private async Task<Guid> NewAgencyAsync(string label)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var agency = Agency.Register(new AgencyDetails($"Agence {label} {Guid.NewGuid():N}", "Sousse")).Value;
        db.Set<Agency>().Add(agency);
        db.Set<Location>().Add(Location.Create(agency.Id, "S" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), "Agence", LocationKind.AgencyOffice).Value);
        await db.SaveChangesAsync(Ct);
        return agency.Id;
    }

    /// <summary>A conversation of <paramref name="agencyId"/> with one customer message, written as the system.</summary>
    private async Task<Guid> ConversationOfAsync(Guid agencyId, string number, string text)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var now = DateTimeOffset.UtcNow;
        var conversation = Conversation.Start(agencyId, number, "Client Sousse", now).Value;
        var message = conversation.ReceiveCustomerMessage(NewMetaId(), TextContent.Received(text), now, null, now);
        db.AddRange(conversation, message);
        await db.SaveChangesAsync(Ct);
        return conversation.Id;
    }
}
