using System.Net;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Paging;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Inbox;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Domain.Messaging;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Messaging.WhatsAppTestData;

namespace CarRental.Integration.Tests.Messaging;

/// <summary>
/// The shared inbox on SQL Server: the list (last message, unread, window) and the thread, a takeover and a reply sent
/// through the fake with its statuses, the audit of the agent's actions, and the nightly retention purge.
/// </summary>
public sealed class WhatsAppInboxIntegrationTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_takes_over_replies_through_the_fake_and_hands_back_with_audit()
    {
        var waId = NewWaId();
        using var client = host.CreateClient();
        (await PostAsync(client, Text(waId, "Bonjour, je voudrais modifier ma réservation", "Leila"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var conversation = await WaitForAsync(() => ConversationAsync(host, waId, Ct), c => c is not null, "the conversation", Ct);
        var agent = Agent();

        var listed = await RunAsync<WhatsAppInboxQueries, Result<PagedResult<InboxConversationItem>>>(agent, q => q.ListAsync(new InboxFilter(Search: waId[^7..]), new PageRequest(), Ct));
        var item = listed.Value.Items.ShouldHaveSingleItem();
        item.ProfileName.ShouldBe("Leila");
        item.UnreadCount.ShouldBe(1);
        item.LastMessage!.Text.ShouldBe("Bonjour, je voudrais modifier ma réservation");
        item.WindowClosesAtUtc.ShouldNotBeNull();

        (await RunAsync<WhatsAppInboxService, Result>(agent, s => s.TakeOverAsync(conversation!.Id, Ct))).IsSuccess.ShouldBeTrue();
        (await RunAsync<WhatsAppInboxService, Result>(agent, s => s.SendTextAsync(conversation!.Id, "Bonjour Leila, je regarde votre dossier.", Ct))).IsSuccess.ShouldBeTrue();
        var sent = await WaitForAsync(() => Task.FromResult(host.Factory.WhatsApp.SentTo(waId)), list => list.Count > 0, "the reply through the fake", Ct);
        sent.ShouldHaveSingleItem().Content.ShouldBeOfType<TextContent>().Body.ShouldBe("Bonjour Leila, je regarde votre dossier.");
        await WaitForAsync(
            async () => (await MessagesAsync(host, conversation!.Id, Ct)).Single(m => m.Author == MessageAuthor.Agent).Status,
            status => status == MessageStatus.Delivered,
            "the reply delivered",
            Ct);
        (await RunAsync<WhatsAppInboxService, Result>(agent, s => s.HandBackToBotAsync(conversation!.Id, Ct))).IsSuccess.ShouldBeTrue();

        var details = (await RunAsync<WhatsAppInboxQueries, Result<InboxConversationDetails>>(agent, q => q.GetAsync(conversation!.Id, Ct))).Value;
        details.Mode.ShouldBe(ConversationModeInfo.Bot);
        details.UnreadCount.ShouldBe(0);
        details.Messages.Select(m => m.Author).ShouldBe([MessageAuthorInfo.Customer, MessageAuthorInfo.Agent]);
        details.Messages[1].AuthorName.ShouldBe(agent.UserName);
        details.Messages[1].Status.ShouldBe(MessageStatusInfo.Delivered);

        await using var scope = host.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<AuditLog>()
            .AsNoTracking()
            .Where(a => a.EntityId == conversation!.Id.ToString() && a.Action.StartsWith("messaging."))
            .OrderBy(a => a.Id)
            .Select(a => new { a.Action, a.UserId, a.Details })
            .ToListAsync(Ct);
        audit.Select(a => a.Action).ShouldBe(
        [
            MessagingAuditActions.ConversationTakenOver,
            MessagingAuditActions.MessageSentByAgent,
            MessagingAuditActions.ConversationHandedBack,
        ]);
        audit.ShouldAllBe(a => a.UserId == agent.UserId && !a.Details!.Contains("Leila"));
        var rows = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<AuditLog>()
            .AsNoTracking()
            .Where(a => a.EntityType == nameof(Conversation) && a.EntityId == conversation!.Id.ToString() && a.Action == AuditActions.EntityCreated)
            .Select(a => a.After)
            .ToListAsync(Ct);
        rows.ShouldHaveSingleItem().ShouldNotBeNull().ShouldNotContain(waId, customMessage: "the number is masked in the audit log");
    }

    [Fact]
    public async Task The_retention_job_purges_old_exchanges_and_unlinks_anonymized_persons()
    {
        var oldNumber = NewWaId();
        var recentNumber = NewWaId();
        var anonymizedNumber = NewWaId();
        var longAgo = DateTimeOffset.UtcNow.AddDays(-400);
        Guid oldId;
        Guid recentId;
        Guid oldMessageOfRecent;
        Guid anonymizedConversation;
        var personId = await NewPersonAsync(host, anonymizedNumber, consent: false, Ct);
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            var old = Conversation.Start(oldNumber, "Ancien", longAgo).Value;
            var oldMessage = old.ReceiveCustomerMessage(NewMetaId(), TextContent.Received("Il y a longtemps"), longAgo, null, longAgo);
            var recent = Conversation.Start(recentNumber, "Récent", longAgo).Value;
            var ancient = recent.ReceiveCustomerMessage(NewMetaId(), TextContent.Received("Vieux message"), longAgo, null, longAgo);
            var fresh = recent.ReceiveCustomerMessage(NewMetaId(), TextContent.Received("Message récent"), DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow);
            var linked = Conversation.Start(anonymizedNumber, null, DateTimeOffset.UtcNow).Value;
            linked.LinkPerson(personId, null);
            db.AddRange(old, recent, linked);
            db.AddRange(oldMessage, ancient, fresh);
            await db.SaveChangesAsync(Ct);
            (oldId, recentId, oldMessageOfRecent, anonymizedConversation) = (old.Id, recent.Id, ancient.Id, linked.Id);
        }

        // Anonymization is a manager's right: the system actor of the tests holds it.
        await using (var scope = host.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<CarRental.Application.Customers.Privacy.PersonalDataService>()
                .AnonymizeAsync(personId, "Demande de la personne", Ct)).IsSuccess.ShouldBeTrue();
        }

        await using (var scope = host.CreateScope())
        {
            var descriptor = scope.ServiceProvider.GetServices<RecurringJobDescriptor>().Single(d => d.JobId == "messaging.purge_conversations");
            await ((IRecurringJob)scope.ServiceProvider.GetRequiredService(descriptor.JobType)).ExecuteAsync(Ct);
        }

        await using var check = host.CreateScope();
        var context = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        (await context.Set<Conversation>().AnyAsync(c => c.Id == oldId, Ct)).ShouldBeFalse();
        (await context.Set<Message>().Where(m => m.ConversationId == recentId).Select(m => m.Text).ToListAsync(Ct)).ShouldBe(["Message récent"]);
        (await context.Set<Message>().AnyAsync(m => m.Id == oldMessageOfRecent, Ct)).ShouldBeFalse();
        (await context.Set<Conversation>().SingleAsync(c => c.Id == anonymizedConversation, Ct)).PersonId.ShouldBeNull();
        (await context.Set<AuditLog>().AnyAsync(a => a.Action == MessagingAuditActions.ConversationsPurged, Ct)).ShouldBeTrue();
    }

    private async Task<T> RunAsync<TService, T>(CarRental.Application.Common.Security.UserContextSnapshot actor, Func<TService, Task<T>> operation)
        where TService : notnull
    {
        await using var scope = host.CreateScope(actor);
        return await operation(scope.ServiceProvider.GetRequiredService<TService>());
    }
}
