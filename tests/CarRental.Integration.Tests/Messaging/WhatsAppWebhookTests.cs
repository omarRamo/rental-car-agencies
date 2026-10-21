using System.Net;
using CarRental.Application.Messaging.Webhooks;
using CarRental.Domain.Messaging;
using CarRental.Infrastructure.Messaging;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence;
using CarRental.Web.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Messaging.WhatsAppTestData;

namespace CarRental.Integration.Tests.Messaging;

/// <summary>
/// The webhook on the real host and SQL Server: the verification handshake, signatures checked on the raw body with a
/// real HMAC (accepted, refused before anything is stored), duplicate deliveries ignored, a customer message reaching
/// the conversation of an existing person, and statuses updating the messages sent.
/// </summary>
public sealed class WhatsAppWebhookTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_verification_handshake_echoes_the_challenge_for_the_configured_token_only()
    {
        using var client = host.CreateClient();

        var accepted = await client.GetAsync(
            new Uri($"{WebhookRoute}?hub.mode=subscribe&hub.verify_token={WhatsAppDevelopmentKeys.VerifyToken}&hub.challenge=1158201444", UriKind.Relative),
            Ct);
        var wrongToken = await client.GetAsync(new Uri($"{WebhookRoute}?hub.mode=subscribe&hub.verify_token=not-the-token&hub.challenge=1158201444", UriKind.Relative), Ct);
        var wrongMode = await client.GetAsync(
            new Uri($"{WebhookRoute}?hub.mode=unsubscribe&hub.verify_token={WhatsAppDevelopmentKeys.VerifyToken}&hub.challenge=1158201444", UriKind.Relative),
            Ct);
        var noChallenge = await client.GetAsync(new Uri($"{WebhookRoute}?hub.mode=subscribe&hub.verify_token={WhatsAppDevelopmentKeys.VerifyToken}", UriKind.Relative), Ct);

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await accepted.Content.ReadAsStringAsync(Ct)).ShouldBe("1158201444");
        accepted.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        wrongToken.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        wrongMode.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        noChallenge.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_signed_message_is_accepted_at_once_and_reaches_the_conversation_of_the_person_with_this_phone()
    {
        var waId = NewWaId();
        var personId = await NewPersonAsync(host, waId, consent: false, Ct);
        var metaId = NewMetaId();
        using var client = host.CreateClient();

        var response = await PostAsync(client, Text(waId, "Bonjour, une voiture pour samedi ?", "Hédi", metaId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var conversation = await WaitForAsync(() => ConversationAsync(host, waId, Ct), c => c is not null, "the conversation", Ct);
        conversation!.PersonId.ShouldBe(personId);
        conversation.ProfileName.ShouldBe("Hédi");
        conversation.Mode.ShouldBe(ConversationMode.Bot);
        conversation.WindowClosesAtUtc.ShouldNotBeNull();
        var message = (await MessagesAsync(host, conversation.Id, Ct)).ShouldHaveSingleItem();
        message.MetaMessageId.ShouldBe(metaId);
        message.Text.ShouldBe("Bonjour, une voiture pour samedi ?");
        message.Content.ShouldBeOfType<TextContent>();
    }

    [Fact]
    public async Task A_blank_text_is_kept_and_its_conversation_stays_readable()
    {
        var waId = NewWaId();
        using var client = host.CreateClient();

        (await PostAsync(client, Text(waId, " 　 "), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var conversation = await WaitForAsync(() => ConversationAsync(host, waId, Ct), c => c is not null, "the conversation", Ct);
        var message = (await MessagesAsync(host, conversation!.Id, Ct)).ShouldHaveSingleItem();
        message.Content.ShouldBeOfType<TextContent>().Body.ShouldBeEmpty();
        await using var scope = host.CreateScope(Agent());
        var details = await scope.ServiceProvider.GetRequiredService<CarRental.Application.Messaging.Inbox.WhatsAppInboxQueries>().GetAsync(conversation.Id, Ct);
        details.IsSuccess.ShouldBeTrue();
        details.Value.Messages.ShouldHaveSingleItem().Kind.ShouldBe(CarRental.Application.Messaging.Contracts.MessageKindInfo.Text);
    }

    [Fact]
    public async Task An_unsigned_or_wrongly_signed_delivery_is_refused_before_anything_is_stored()
    {
        var waId = NewWaId();
        var metaId = NewMetaId();
        var payload = Text(waId, "Je suis un faux", metaId: metaId);
        using var client = host.CreateClient();

        var unsigned = await PostAsync(client, payload, Ct, sign: false);
        var wrongSecret = await PostAsync(client, payload, Ct, signature: WhatsAppWebhookSecurity.ComputeSignature(System.Text.Encoding.UTF8.GetBytes(payload), "another-secret"));
        var garbage = await PostAsync(client, payload, Ct, signature: "sha256=zz");
        var otherBody = await PostAsync(client, payload, Ct, signature: WhatsAppWebhookSecurity.ComputeSignature(System.Text.Encoding.UTF8.GetBytes(payload + " "), WhatsAppDevelopmentKeys.AppSecret));

        unsigned.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        wrongSecret.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        garbage.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        otherBody.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var scope = host.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<OutboxMessage>()
            .AnyAsync(m => m.Type == typeof(WhatsAppWebhookReceived).FullName && m.Payload.Contains(metaId), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_delivery_posted_twice_records_the_message_once()
    {
        var waId = NewWaId();
        var metaId = NewMetaId();
        var payload = Text(waId, "Bonjour", metaId: metaId);
        using var client = host.CreateClient();

        (await PostAsync(client, payload, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(client, payload, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await WaitForAsync(() => PendingDeliveriesAsync(metaId), count => count == 0, "both deliveries processed", Ct);
        var conversation = (await ConversationAsync(host, waId, Ct)).ShouldNotBeNull();
        (await MessagesAsync(host, conversation.Id, Ct)).ShouldHaveSingleItem().MetaMessageId.ShouldBe(metaId);
        conversation.UnreadCount.ShouldBe(1);
    }

    [Fact]
    public async Task Statuses_reported_by_meta_update_the_message_sent_once_each()
    {
        var waId = NewWaId();
        using var client = host.CreateClient();
        (await PostAsync(client, Text(waId, "Bonjour"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var conversation = await WaitForAsync(() => ConversationAsync(host, waId, Ct), c => c is not null, "the conversation", Ct);
        var agent = Agent();
        await using (var scope = host.CreateScope(agent))
        {
            var inbox = scope.ServiceProvider.GetRequiredService<CarRental.Application.Messaging.Inbox.WhatsAppInboxService>();
            (await inbox.TakeOverAsync(conversation!.Id, Ct)).IsSuccess.ShouldBeTrue();
            (await inbox.SendTextAsync(conversation.Id, "Bonjour, ici l'agence.", Ct)).IsSuccess.ShouldBeTrue();
        }

        // The fake reports "sent" then "delivered" like Meta; the test posts "read" as a signed delivery, twice.
        var sent = await WaitForAsync(
            async () => (await MessagesAsync(host, conversation.Id, Ct)).SingleOrDefault(m => m.Author == MessageAuthor.Agent),
            m => m is { Status: MessageStatus.Delivered },
            "the delivered status",
            Ct);
        host.Factory.WhatsApp.SentTo(waId).ShouldHaveSingleItem().MetaMessageId.ShouldBe(sent!.MetaMessageId);
        (await PostAsync(client, Status(sent.MetaMessageId!, waId, "read"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(client, Status(sent.MetaMessageId!, waId, "read"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var read = await WaitForAsync(
            async () => (await MessagesAsync(host, conversation.Id, Ct)).Single(m => m.Id == sent.Id),
            m => m.Status == MessageStatus.Read,
            "the read status",
            Ct);
        await WaitForAsync(() => PendingDeliveriesAsync(sent.MetaMessageId!), count => count == 0, "the status deliveries processed", Ct);
        read = (await MessagesAsync(host, conversation.Id, Ct)).Single(m => m.Id == sent.Id);
        read.StatusChanges.Select(c => c.Status).ShouldBe([MessageStatus.Sent, MessageStatus.Delivered, MessageStatus.Read], ignoreOrder: true);
        read.PricingCategory.ShouldBe("service");
        read.IsBillable.ShouldBe(true);
        read.ReadAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_delivery_larger_than_a_megabyte_is_refused()
    {
        using var client = host.CreateClient();

        var response = await PostAsync(client, Text(NewWaId(), new string('a', WhatsAppWebhookEndpointsLimit + 10)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public void The_webhook_is_anonymous_and_limited_by_a_policy_of_its_own()
    {
        var endpoints = host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.TrimEnd('/') == WebhookRoute)
            .ToList();

        endpoints.Count.ShouldBe(2, "GET for the handshake, POST for the deliveries");
        endpoints.ShouldAllBe(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()!.PolicyName == WhatsAppWebhookEndpoints.RateLimitPolicy);
        endpoints.ShouldAllBe(e => e.Metadata.GetMetadata<IAllowAnonymous>() != null);
    }

    private const int WhatsAppWebhookEndpointsLimit = WhatsAppWebhookEndpoints.MaxBodyBytes;

    /// <summary>Stored webhook deliveries mentioning <paramref name="marker"/> not processed yet.</summary>
    private async Task<int> PendingDeliveriesAsync(string marker)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<OutboxMessage>()
            .CountAsync(m => m.Type == typeof(WhatsAppWebhookReceived).FullName && m.Payload.Contains(marker) && m.ProcessedAtUtc == null, Ct);
    }
}
