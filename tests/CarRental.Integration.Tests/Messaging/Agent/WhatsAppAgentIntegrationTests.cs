using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Messaging.Agent.Knowledge;
using CarRental.Application.Messaging.Agent.Turns;
using CarRental.Application.Messaging.Inbox;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using CarRental.Domain.Messaging.Agent;
using CarRental.Infrastructure.Messaging;
using CarRental.Infrastructure.Messaging.Agent;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Bookings;
using CarRental.Integration.Tests.Public;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Messaging.WhatsAppTestData;

// Not "…Messaging.Agent": that namespace would hide WhatsAppTestData.Agent() from the other messaging tests.
namespace CarRental.Integration.Tests.Messaging.AiAgent;

/// <summary>
/// The WhatsApp agent on the real host and SQL Server, with the scripted model (<c>Ai:Mode=Fake</c>; the bot answers the
/// numbers a test scripts only): customer messages answered by the bot turn executor outside the outbox, turns of
/// different conversations in parallel and those of one conversation one after the other, the takeover that wins over a
/// reply in progress, the real availability, pricing and locked quotes behind the link of the funnel, the validated
/// knowledge only, the status of a file for its own number only, and the SQL Server lock of the turns.
/// </summary>
public sealed partial class WhatsAppAgentIntegrationTests(IntegrationHost host)
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ScriptedChatClient Model => host.Factory.Agent;

    private FakeWhatsApp WhatsApp => host.Factory.WhatsApp;

    [Fact]
    public async Task A_customer_message_is_answered_by_the_executor_with_the_reply_of_the_model()
    {
        var waId = NewWaId();
        Model.Script(waId, ScriptedResponse.Reply("Bonjour ! Pour quelles dates et quel lieu de prise en charge ?"));

        await WhatsApp.SimulateIncomingTextAsync(waId, "Client test", "Bonjour, je cherche une voiture", Ct);

        var sent = await SentAsync(waId, 1);
        sent[0].Content.ShouldBeOfType<TextContent>().Body.ShouldBe("Bonjour ! Pour quelles dates et quel lieu de prise en charge ?");
        var conversation = await WaitForAsync(() => ConversationAsync(host, waId, Ct), c => c is { Usage.BotTurns: 1 }, "the usage of the turn", Ct);
        conversation!.Mode.ShouldBe(ConversationMode.Bot);
        conversation.Usage.CacheReadTokens.ShouldBe(2_400);
        conversation.Usage.AiCostUsd.ShouldBeGreaterThan(0m);
        var messages = await MessagesAsync(host, conversation.Id, Ct);
        messages.Single(m => m.IsInbound).BotTurn.ShouldBe(BotTurnState.Replied);
        messages.Single(m => !m.IsInbound).Author.ShouldBe(MessageAuthor.Bot);
        (await RecordsAsync(conversation.Id)).ShouldHaveSingleItem().Outcome.ShouldBe(AgentTurnOutcome.Replied);
        var request = Model.RequestsFor(waId).ShouldHaveSingleItem();
        request.Messages[0].Text.ShouldStartWith("Tu es l'assistant WhatsApp de Agence Démo Tunis");
        string.Join('\n', request.Messages.Select(m => m.Text)).ShouldNotContain(waId, customMessage: "the number never reaches the model");
    }

    [Fact]
    public async Task Options_found_by_the_real_engines_are_offered_with_buttons_then_locked_behind_the_link_of_the_funnel()
    {
        var waId = NewWaId();
        var (from, to) = (PublicTestData.LocalDate(76), PublicTestData.LocalDate(80));
        Model.Script(
            waId,
            ScriptedResponse.Call("rechercher_disponibilites", $$"""{"lieu_depart":"TUN","depart":"{{from}}","retour":"{{to}}"}"""),
            ScriptedResponse.Reply("Voici mes propositions :\n1. {{rechercher_disponibilites:options[0].categorie}} — {{rechercher_disponibilites:options[0].total_ttc}}"),
            ScriptedResponse.Call("creer_lien_devis", """{"option_id":"A1"}"""),
            ScriptedResponse.Reply("Votre lien sécurisé (prix garanti 24 heures, {{creer_lien_devis:total_ttc}}) : {{creer_lien_devis:lien}}"));

        await WhatsApp.SimulateIncomingTextAsync(waId, "Client test", "Une voiture à l'aéroport de Tunis-Carthage pour 4 jours", Ct);
        var proposal = (await SentAsync(waId, 1))[0].Content.ShouldBeOfType<ButtonsContent>();
        proposal.Body.ShouldContain(" TND");
        var button = proposal.Buttons[0];
        button.Id.ShouldBe("option:A1");
        await WhatsApp.SimulateButtonReplyAsync(waId, "Client test", button.Id, button.Title, Ct);

        var text = (await SentAsync(waId, 2))[1].Content.ShouldBeOfType<TextContent>().Body;
        var link = new Uri(LinkPattern().Match(text).Value);
        link.AbsolutePath.ShouldBe("/fr/reserver");
        var query = QueryHelpers.ParseQuery(link.Query);
        query["lieu"].ToString().ShouldBe("TUN");
        query["depart"].ToString().ShouldBe(from);
        query["retour"].ToString().ShouldBe(to);
        query["src"].ToString().ShouldBe("whatsapp");
        var code = query["devis"].ToString();
        await using (var scope = host.CreateScope())
        {
            var quote = (await scope.ServiceProvider.GetRequiredService<IBookingRequests>().GetLockedQuoteAsync(code, Ct)).ShouldNotBeNull();
            quote.Channel.ShouldBe(SalesChannel.WhatsApp);
            text.ShouldContain(quote.TotalInclVat.Amount.ToString("0.000", CultureInfo.InvariantCulture) + " TND");
        }

        using var client = host.CreateClient();
        (await client.GetAsync(new Uri(link.PathAndQuery, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "the link opens the booking funnel");
    }

    [Fact]
    public async Task Turns_of_different_conversations_run_at_the_same_time()
    {
        var (first, second) = (NewWaId(), NewWaId());
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        async Task BothInProgress(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            await bothStarted.Task.WaitAsync(GateTimeout, cancellationToken);
        }

        Model.Script(first, ScriptedResponse.Reply("Bonjour !").WaitingFor(BothInProgress));
        Model.Script(second, ScriptedResponse.Reply("Bonjour !").WaitingFor(BothInProgress));

        await WhatsApp.SimulateIncomingTextAsync(first, "Client test", "Bonjour", Ct);
        await WhatsApp.SimulateIncomingTextAsync(second, "Client test", "Bonjour", Ct);

        await SentAsync(first, 1);
        await SentAsync(second, 1);
        bothStarted.Task.IsCompletedSuccessfully.ShouldBeTrue("the second model call started while the first one was in progress");
        Model.RequestsFor(first).Count.ShouldBe(1);
        Model.RequestsFor(second).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Two_bursts_of_one_conversation_are_answered_once_each_one_after_the_other()
    {
        var waId = NewWaId();
        var firstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Model.Script(
            waId,
            ScriptedResponse.Reply("Bonjour ! Pour quelles dates ?").WaitingFor(async cancellationToken =>
            {
                firstCall.TrySetResult();
                await release.Task.WaitAsync(GateTimeout, cancellationToken);
            }),
            ScriptedResponse.Reply("Merci, c'est noté."));

        await WhatsApp.SimulateIncomingTextAsync(waId, "Client test", "Bonjour", Ct);
        await firstCall.Task.WaitAsync(GateTimeout, Ct);
        await WhatsApp.SimulateIncomingTextAsync(waId, "Client test", "Du 3 au 7 juillet", Ct);
        await WhatsApp.SimulateIncomingTextAsync(waId, "Client test", "Boîte automatique", Ct);
        var conversation = (await ConversationAsync(host, waId, Ct)).ShouldNotBeNull();
        await WaitForAsync(() => MessagesAsync(host, conversation.Id, Ct), m => m.Count(x => x.IsInbound) == 3, "the second burst recorded", Ct);
        release.TrySetResult();

        var sent = await SentAsync(waId, 2);
        sent.Select(s => s.Content.ShouldBeOfType<TextContent>().Body).ShouldBe(["Bonjour ! Pour quelles dates ?", "Merci, c'est noté."]);
        Model.MaxConcurrency(waId).ShouldBe(1, "never two turns of one conversation at once");
        var second = Model.RequestsFor(waId)[1].Messages.Select(m => m.Text).ToList();
        second.ShouldContain(t => t.Contains("Bonjour ! Pour quelles dates ?", StringComparison.Ordinal), "the first reply is in the history of the second turn");
        second.ShouldContain(t => t.Contains("Du 3 au 7 juillet", StringComparison.Ordinal) && t.Contains("Boîte automatique", StringComparison.Ordinal));
        var inbound = (await MessagesAsync(host, conversation.Id, Ct)).Where(m => m.IsInbound).Select(m => m.BotTurn).ToList();
        inbound.ShouldBe([BotTurnState.Replied, BotTurnState.Superseded, BotTurnState.Replied]);
    }

    [Fact]
    public async Task A_takeover_during_the_model_call_wins_over_the_reply()
    {
        var waId = NewWaId();
        var call = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Model.Script(waId, ScriptedResponse.Reply("Réponse du bot qui ne doit pas partir").WaitingFor(async cancellationToken =>
        {
            call.TrySetResult();
            await release.Task.WaitAsync(GateTimeout, cancellationToken);
        }));
        await WhatsApp.SimulateIncomingTextAsync(waId, "Client test", "Bonjour", Ct);
        await call.Task.WaitAsync(GateTimeout, Ct);
        var conversation = (await ConversationAsync(host, waId, Ct)).ShouldNotBeNull();

        await using (var scope = host.CreateScope(Agent()))
        {
            (await scope.ServiceProvider.GetRequiredService<WhatsAppInboxService>().TakeOverAsync(conversation.Id, Ct)).IsSuccess.ShouldBeTrue();
        }

        release.TrySetResult();
        conversation = await WaitForAsync(() => ConversationAsync(host, waId, Ct), c => c is { Usage.BotTurns: 1 }, "the turn applied", Ct);
        conversation!.Mode.ShouldBe(ConversationMode.Human);
        (await MessagesAsync(host, conversation.Id, Ct)).Single(m => m.IsInbound).BotTurn.ShouldBe(BotTurnState.Silenced);
        WhatsApp.SentTo(waId).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_validated_answers_of_the_knowledge_base_reach_the_model()
    {
        var marker = NewName().ToLowerInvariant();
        var manager = UserContextSnapshot.ForUser(Guid.CreateVersion7(), $"gerant-{marker}@tests.local", RolePresets.PermissionsOf(Roles.Manager));
        await using (var scope = host.CreateScope(manager))
        {
            var knowledge = scope.ServiceProvider.GetRequiredService<KnowledgeBaseService>();
            var validated = await knowledge.CreateAsync(new KnowledgeEntryForm($"k{marker}_ok", "Réponse validée", marker, $"Réponse validée {marker}.", $"Validated answer {marker}.", null), Ct);
            (await knowledge.ValidateAsync(validated.Value, Ct)).IsSuccess.ShouldBeTrue();
            (await knowledge.CreateAsync(new KnowledgeEntryForm($"k{marker}_draft", "Brouillon", marker, $"Brouillon {marker}.", $"Draft {marker}.", null), Ct)).IsSuccess.ShouldBeTrue();
        }

        var waId = NewWaId();
        Model.Script(waId, ScriptedResponse.Call("infos_agence", $$"""{"sujet":"{{marker}}"}"""), ScriptedResponse.Reply("Voici ce que l'agence a validé."));

        await WhatsApp.SimulateIncomingTextAsync(waId, "Client test", $"Une question sur {marker}", Ct);

        await SentAsync(waId, 1);
        var result = ToolResult(waId, 1);
        result.ShouldContain($"Réponse validée {marker}.");
        result.ShouldNotContain($"Brouillon {marker}");
    }

    [Fact]
    public async Task The_status_of_a_file_is_given_to_the_number_of_its_driver_only()
    {
        var driverWaId = NewWaId();
        var agency = await BookingWorld.CreateAsync(host, Ct);
        var driver = await NewDriverAsync("+" + driverWaId);
        var booking = await BookingWorld.CreateAsync(host, BookingWorld.Quick(agency, driver, BookingWorld.InDays(85), BookingWorld.InDays(88)), Ct);
        var otherWaId = NewWaId();
        foreach (var waId in new[] { driverWaId, otherWaId })
        {
            Model.Script(
                waId,
                ScriptedResponse.Call("statut_reservation", $$"""{"reference":"{{booking.Number}}"}"""),
                ScriptedResponse.Reply(waId == driverWaId ? "Votre réservation {{statut_reservation:reference}} : {{statut_reservation:etat}}." : "Je ne trouve pas cette réservation."));
        }

        await WhatsApp.SimulateIncomingTextAsync(driverWaId, "Client test", $"Où en est ma réservation {booking.Number} ?", Ct);
        await WhatsApp.SimulateIncomingTextAsync(otherWaId, "Client test", $"Où en est la réservation {booking.Number} ?", Ct);

        (await SentAsync(driverWaId, 1))[0].Content.ShouldBeOfType<TextContent>().Body.ShouldBe($"Votre réservation {booking.Number} : confirmée par l'agence.");
        await SentAsync(otherWaId, 1);
        ToolResult(driverWaId, 1).ShouldContain("\"statut\":\"trouve\"");
        var refused = ToolResult(otherWaId, 1);
        refused.ShouldContain("\"statut\":\"introuvable\"");
        refused.ShouldNotContain(booking.Number);
    }

    [Fact]
    public async Task The_turn_lock_of_a_conversation_is_held_on_sql_server_until_released()
    {
        var turnLock = host.Factory.Services.GetRequiredService<IConversationTurnLock>();
        turnLock.ShouldBeOfType<SqlConversationTurnLock>();
        var (conversation, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        var lease = (await turnLock.TryAcquireAsync(conversation, Ct)).ShouldNotBeNull();
        (await turnLock.TryAcquireAsync(conversation, Ct)).ShouldBeNull("another session holds it");
        var otherLease = (await turnLock.TryAcquireAsync(other, Ct)).ShouldNotBeNull("one lock per conversation");
        await lease.DisposeAsync();
        var again = (await turnLock.TryAcquireAsync(conversation, Ct)).ShouldNotBeNull("released");

        await again.DisposeAsync();
        await otherLease.DisposeAsync();
    }

    [Fact]
    public async Task The_queue_of_the_executor_runs_on_sql_server()
    {
        await using var scope = host.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IBotTurnQueue>();
        var options = new BotTurnOptions();

        var due = await queue.DueAsync(options, [Guid.CreateVersion7()], 5, Ct);
        var next = await queue.NextDueAtAsync(options, [Guid.CreateVersion7()], Ct);

        due.Count.ShouldBeLessThanOrEqualTo(5);
        (next is null || next > DateTimeOffset.UnixEpoch).ShouldBeTrue();
    }

    /// <summary>Waits until <paramref name="count"/> messages were sent to <paramref name="waId"/>.</summary>
    private Task<IReadOnlyList<FakeWhatsAppMessage>> SentAsync(string waId, int count) =>
        WaitForAsync(() => Task.FromResult(WhatsApp.SentTo(waId)), sent => sent.Count >= count, $"{count} message(s) of the bot", Ct);

    /// <summary>The tool result the model received in its request number <paramref name="request"/> (0-based) for <paramref name="waId"/>.</summary>
    private string ToolResult(string waId, int request) =>
        Model.RequestsFor(waId)[request].Messages
            .SelectMany(m => m.Contents)
            .OfType<Microsoft.Extensions.AI.FunctionResultContent>()
            .Select(r => r.Result as string)
            .OfType<string>()
            .ShouldHaveSingleItem();

    private async Task<List<AgentTurnRecord>> RecordsAsync(Guid conversationId)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<AgentTurnRecord>()
            .AsNoTracking()
            .Where(r => r.ConversationId == conversationId)
            .ToListAsync(Ct);
    }

    /// <summary>An eligible Tunisian driver with this phone (CIN, licence held for ten years), created by an agent.</summary>
    private async Task<Guid> NewDriverAsync(string phone)
    {
        await using var scope = host.CreateScope(BookingWorld.Agent);
        var registration = scope.ServiceProvider.GetRequiredService<ICustomerRegistration>();
        var today = TunisTime.Today(TimeProvider.System);
        var person = await registration.FindOrCreatePersonAsync(
            new PersonRegistrationRequest(
                NewName(),
                NewName(),
                "TN",
                new DateOnly(1990, 1, 15),
                phone,
                Document: new IdentityDocumentInput(
                    IdentityDocumentTypeInfo.NationalId,
                    RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture),
                    "TN",
                    today.AddYears(-5))),
            Ct);
        person.IsSuccess.ShouldBeTrue(person.IsFailure ? person.Error.ToString() : null);
        var license = await registration.AddOrReplaceDocumentAsync(
            person.Value.PersonId,
            new IdentityDocumentInput(
                IdentityDocumentTypeInfo.DrivingLicense,
                $"{RandomNumberGenerator.GetInt32(10, 99)}/{RandomNumberGenerator.GetInt32(100_000, 999_999)}",
                "TN",
                today.AddYears(-10)),
            cancellationToken: Ct);
        license.IsSuccess.ShouldBeTrue(license.IsFailure ? license.Error.ToString() : null);
        return person.Value.PersonId;
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();
}
