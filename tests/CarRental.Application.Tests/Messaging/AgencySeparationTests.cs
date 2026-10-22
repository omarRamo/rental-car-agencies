using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Messaging;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Notifications;
using CarRental.Application.Messaging.Webhooks;
using CarRental.Application.Tests.Fakes;
using CarRental.Application.Tests.Messaging.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CarRental.Application.Tests.Messaging;

/// <summary>
/// WhatsApp is an optional module of the agencies (ADR-0067): the platform's number serves one agency (configured, or
/// the only one with the module on), whose conversations and work stay its own; an agency suspended, without the module,
/// or with the module but not served by the number receives nothing, sends nothing and is notified of nothing, and its
/// data is kept.
/// </summary>
public sealed class AgencySeparationTests : IAsyncDisposable
{
    private const string Customer = "21655123456";

    private readonly MessagingHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task Without_configuration_the_only_agency_with_the_module_on_serves_the_number()
    {
        var (first, second, third) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var agencies = new StaticAgencyDirectory(first);
        agencies.Set(second, AgencyModuleCatalog.Defaults);
        var routing = Routing(agencies, new WhatsAppRoutingOptions());

        (await routing.ResolveAsync(Ct)).ShouldBe(WhatsAppRoute.To(first));

        agencies.Set(third, AgencyModuleCatalog.Every);
        (await routing.ResolveAsync(Ct)).ShouldBe(WhatsAppRoute.None(WhatsAppRouteStatus.Ambiguous), "two agencies could own the number");

        agencies.Set(third, AgencyModuleCatalog.Every, active: false);
        (await routing.ResolveAsync(Ct)).ShouldBe(WhatsAppRoute.To(first), "a suspended agency serves nothing");

        agencies.Set(first, AgencyModuleCatalog.Defaults);
        (await routing.ResolveAsync(Ct)).ShouldBe(WhatsAppRoute.None(WhatsAppRouteStatus.NoAgency));
    }

    [Fact]
    public async Task A_configured_agency_serves_the_number_only_while_it_is_active_with_the_module_on()
    {
        var (served, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var agencies = new StaticAgencyDirectory(served, other);
        var byId = Routing(agencies, new WhatsAppRoutingOptions { AgencyId = served });
        var bySlug = Routing(agencies, new WhatsAppRoutingOptions { AgencySlug = $"AGENCE-{served:N}" });

        (await byId.ResolveAsync(Ct)).ShouldBe(WhatsAppRoute.To(served), "the configuration settles what two open agencies would leave ambiguous");
        (await bySlug.ResolveAsync(Ct)).ShouldBe(WhatsAppRoute.To(served));

        agencies.Set(served, AgencyModuleCatalog.Every & ~AgencyModules.WhatsApp);
        (await byId.ResolveAsync(Ct)).ShouldBe(WhatsAppRoute.None(WhatsAppRouteStatus.ModuleOff), "never another agency instead");

        agencies.Set(served, AgencyModuleCatalog.Every, active: false);
        (await bySlug.ResolveAsync(Ct)).ShouldBe(WhatsAppRoute.None(WhatsAppRouteStatus.ModuleOff));

        (await Routing(agencies, new WhatsAppRoutingOptions { AgencyId = Guid.CreateVersion7() }).ResolveAsync(Ct))
            .ShouldBe(WhatsAppRoute.None(WhatsAppRouteStatus.UnknownAgency));
    }

    [Fact]
    public async Task A_message_that_no_agency_serves_is_recorded_nowhere_until_one_does()
    {
        _harness.Agencies.Set(_harness.AgencyId, AgencyModuleCatalog.Every & ~AgencyModules.WhatsApp);
        await _harness.CustomerWritesAsync(Customer, "Bonjour, vous êtes là ?");

        var other = Guid.CreateVersion7();
        _harness.Agencies.Set(_harness.AgencyId, AgencyModuleCatalog.Every);
        _harness.Agencies.Set(other, AgencyModuleCatalog.Every);
        await _harness.CustomerWritesAsync(Customer, "Allô ?");

        _harness.Conversations.ShouldBeEmpty("module off, then two agencies with the module on: nobody owns the number");
        _harness.Messages.ShouldBeEmpty();
        _harness.Responder.Turns.ShouldBeEmpty();

        _harness.Agencies.Set(other, AgencyModuleCatalog.Defaults);
        await _harness.CustomerWritesAsync(Customer, "Bonjour !");

        var conversation = _harness.Conversations.ShouldHaveSingleItem();
        conversation.AgencyId.ShouldBe(_harness.AgencyId);
        _harness.Messages.ShouldHaveSingleItem().AgencyId.ShouldBe(_harness.AgencyId);
    }

    [Fact]
    public async Task A_message_queued_before_the_module_was_switched_off_never_leaves()
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

        _harness.Agencies.Set(_harness.AgencyId, AgencyModuleCatalog.Every & ~AgencyModules.WhatsApp);
        await _harness.HandleAsync(queued);

        _harness.WhatsApp.Sent.ShouldBeEmpty();
        var reply = _harness.Messages.Single(m => m.Id == queued.MessageId);
        reply.Status.ShouldBe(MessageStatus.Failed);
        reply.FailureCode.ShouldBe(MessagingErrors.ModuleOff.Code);
    }

    [Fact]
    public async Task A_customer_message_of_an_agency_whose_module_was_switched_off_waits_without_a_bot_turn()
    {
        var payload = $"payload-{Guid.NewGuid():N}";
        _harness.Parser.Batches[payload] = new WhatsAppWebhookBatch(
            [new InboundWhatsAppMessage($"wamid.{Guid.NewGuid():N}", Customer, "Rania", _harness.Clock.GetUtcNow(), TextContent.Received("Bonjour"))],
            [],
            0);
        await _harness.HandleAsync(new WhatsAppWebhookReceived(payload, _harness.Clock.GetUtcNow()));
        var turn = _harness.Db.PublishedEvents.OfType<CustomerMessageReceived>().Single();
        _harness.Db.PublishedEvents.Clear();

        _harness.Agencies.Set(_harness.AgencyId, AgencyModuleCatalog.Every, active: false);
        await _harness.HandleAsync(turn);
        await _harness.DrainAsync();

        _harness.Responder.Turns.ShouldBeEmpty();
        _harness.WhatsApp.Sent.ShouldBeEmpty();
        _harness.Messages.ShouldHaveSingleItem().BotTurn.ShouldBe(BotTurnState.Pending, "kept for the agency: answered once it is open again");
    }

    [Fact]
    public async Task An_agency_without_its_whatsapp_module_is_notified_of_nothing()
    {
        var personId = _harness.AddPerson("+21622222009", consent: true);
        var tomorrow = TunisTime.FromLocal(TunisTime.Today(_harness.Clock).AddDays(1), new TimeOnly(10, 0));
        var booking = _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Confirmed, tomorrow, tomorrow.AddDays(3), personId);
        _harness.Agencies.Set(_harness.AgencyId, AgencyModuleCatalog.Every & ~AgencyModules.WhatsApp);

        var outcome = await _harness.RunAsync<BookingNotifier, NotificationOutcome>(n => n.NotifyAsync(booking.Id, NotificationKind.BookingConfirmed, Ct));
        await _harness.HandleAsync(new BookingStatusChanged(
            booking.Id, booking.AgencyId, booking.Number, booking.Channel, BookingStatusInfo.PendingValidation, BookingStatusInfo.Confirmed, booking.TotalInclVat, booking.PaymentDueAt, DateTimeOffset.UtcNow));
        await using (var scope = _harness.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<PickupReminderJob>().ExecuteAsync(Ct);
        }

        await _harness.DrainAsync();

        outcome.ShouldBe(NotificationOutcome.Disabled);
        _harness.WhatsApp.Sent.ShouldBeEmpty();
        _harness.Conversations.ShouldBeEmpty();
        _harness.Db.Rows<BookingNotification>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_agency_the_number_serves_is_notified_and_its_customers_replies_come_back_to_it()
    {
        // Two agencies with the WhatsApp module on; the number designates the other one.
        var other = Guid.CreateVersion7();
        _harness.Agencies.Set(other, AgencyModuleCatalog.Every);
        _harness.Routing.AgencyId = other;
        var personId = _harness.AddPerson("+21622222010", consent: true);
        var tomorrow = TunisTime.FromLocal(TunisTime.Today(_harness.Clock).AddDays(1), new TimeOnly(10, 0));
        var booking = _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Confirmed, tomorrow, tomorrow.AddDays(3), personId);

        var outcome = await _harness.RunAsync<BookingNotifier, NotificationOutcome>(n => n.NotifyAsync(booking.Id, NotificationKind.BookingConfirmed, Ct));
        await _harness.HandleAsync(new BookingStatusChanged(
            booking.Id, booking.AgencyId, booking.Number, booking.Channel, BookingStatusInfo.PendingValidation, BookingStatusInfo.Confirmed, booking.TotalInclVat, booking.PaymentDueAt, DateTimeOffset.UtcNow));
        await using (var scope = _harness.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<PickupReminderJob>().ExecuteAsync(Ct);
        }

        await _harness.DrainAsync();

        outcome.ShouldBe(NotificationOutcome.Disabled, "its customer's reply would reach the other agency");
        _harness.WhatsApp.Sent.ShouldBeEmpty();
        _harness.Conversations.ShouldBeEmpty();
        _harness.Db.Rows<BookingNotification>().ShouldBeEmpty();

        // The number serves the agency again: the notification leaves, and the customer's reply reaches its conversation.
        _harness.Routing.AgencyId = _harness.AgencyId;
        (await _harness.RunAsync<BookingNotifier, NotificationOutcome>(n => n.NotifyAsync(booking.Id, NotificationKind.BookingConfirmed, Ct))).ShouldBe(NotificationOutcome.Queued);
        await _harness.DrainAsync();
        await _harness.CustomerWritesAsync("21622222010", "Merci, à demain !");

        _harness.WhatsApp.Sent.ShouldHaveSingleItem().Content.ShouldBeOfType<TemplateContent>();
        var conversation = _harness.Conversations.ShouldHaveSingleItem();
        conversation.AgencyId.ShouldBe(_harness.AgencyId);
        _harness.Messages.ShouldAllBe(m => m.ConversationId == conversation.Id && m.AgencyId == _harness.AgencyId);
        _harness.Messages.Count(m => m.Direction == MessageDirection.Inbound).ShouldBe(1);
    }

    [Fact]
    public async Task An_agency_the_number_does_not_serve_gets_no_bot_turn_and_its_queued_replies_never_leave()
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
        _harness.Responder.Turns.Clear();

        // The number is given to another agency (its module on as well) before the reply leaves and before the next turn.
        var other = Guid.CreateVersion7();
        _harness.Agencies.Set(other, AgencyModuleCatalog.Every);
        _harness.Routing.AgencyId = other;
        await _harness.HandleAsync(queued);
        var reply = _harness.Messages.Single(m => m.Id == queued.MessageId);
        var conversationId = reply.ConversationId;
        await using (var scope = _harness.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var conversation = db.Set<Conversation>().Single(c => c.Id == conversationId);
            db.Set<Message>().Add(conversation.ReceiveCustomerMessage($"wamid.{Guid.NewGuid():N}", TextContent.Received("Allô ?"), _harness.Clock.GetUtcNow(), null, _harness.Clock.GetUtcNow()));
            await db.SaveChangesAsync(Ct);
        }

        await _harness.DrainAsync();

        _harness.WhatsApp.Sent.ShouldBeEmpty();
        reply.Status.ShouldBe(MessageStatus.Failed);
        reply.FailureCode.ShouldBe(MessagingErrors.NumberNotServed.Code);
        _harness.Responder.Turns.ShouldBeEmpty("no bot turn for an agency the number does not serve");
        _harness.Messages.Single(m => m.Text == "Allô ?").BotTurn.ShouldBe(BotTurnState.Pending, "kept: answered once the number serves the agency again");
    }

    [Fact]
    public async Task An_agency_the_number_does_not_serve_is_reported_once_per_run_not_per_message()
    {
        var logs = new RecordingLoggerProvider();
        await using var harness = new MessagingHarness(services => services.AddSingleton<ILoggerProvider>(logs));
        var other = Guid.CreateVersion7();
        harness.Agencies.Set(other, AgencyModuleCatalog.Every);
        harness.Routing.AgencyId = other;
        var scopes = harness.Services.GetRequiredService<MessagingScopes>();

        (await scopes.IsOpenAsync(harness.AgencyId, Ct)).ShouldBeFalse();
        (await scopes.IsOpenAsync(harness.AgencyId, Ct)).ShouldBeFalse();
        (await scopes.OpenAgenciesAsync(Ct)).ShouldBe([other]);
        (await scopes.IsOpenAsync(other, Ct)).ShouldBeTrue();

        logs.Warnings(nameof(WhatsAppNumber)).ShouldHaveSingleItem().ShouldContain(harness.AgencyId.ToString());
    }

    [Fact]
    public async Task Switching_the_module_on_serves_the_agency_only_when_the_number_would()
    {
        var (served, other, third) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var agencies = new StaticAgencyDirectory(served);
        agencies.Set(other, AgencyModuleCatalog.Defaults);

        var undesignated = Routing(agencies, new WhatsAppRoutingOptions());
        (await undesignated.OutlookAsync(served, Ct)).ShouldBe(WhatsAppNumberOutlook.Serves);
        (await undesignated.OutlookAsync(other, Ct)).ShouldBe(WhatsAppNumberOutlook.WouldBeAmbiguous, "two agencies with the module: neither would be served");

        var byId = Routing(agencies, new WhatsAppRoutingOptions { AgencyId = other });
        (await byId.OutlookAsync(other, Ct)).ShouldBe(WhatsAppNumberOutlook.Serves, "designated first, then switched on: the way to move the number");
        (await byId.OutlookAsync(served, Ct)).ShouldBe(WhatsAppNumberOutlook.ServesAnotherAgency);
        (await Routing(agencies, new WhatsAppRoutingOptions { AgencySlug = $"agence-{third:N}" }).OutlookAsync(third, Ct))
            .ShouldBe(WhatsAppNumberOutlook.ServesAnotherAgency, "the address names no agency yet");
    }

    [Fact]
    public async Task Designating_the_agency_by_its_address_is_reported_once_with_the_id_to_configure()
    {
        var served = Guid.CreateVersion7();
        var logs = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var routing = new WhatsAppAgencyRouting(
            new StaticAgencyDirectory(served),
            Options.Create(new WhatsAppRoutingOptions { AgencySlug = $"agence-{served:N}" }),
            factory.CreateLogger<WhatsAppAgencyRouting>());

        (await routing.ResolveAsync(Ct)).ShouldBe(WhatsAppRoute.To(served));
        (await routing.ResolveAsync(Ct)).ShouldBe(WhatsAppRoute.To(served));

        var warning = logs.Warnings(nameof(WhatsAppAgencyRouting)).ShouldHaveSingleItem();
        warning.ShouldContain("WhatsApp:AgencyId");
        warning.ShouldContain(served.ToString());
    }

    private static WhatsAppAgencyRouting Routing(StaticAgencyDirectory agencies, WhatsAppRoutingOptions options) =>
        new(agencies, Options.Create(options), NullLogger<WhatsAppAgencyRouting>.Instance);
}
