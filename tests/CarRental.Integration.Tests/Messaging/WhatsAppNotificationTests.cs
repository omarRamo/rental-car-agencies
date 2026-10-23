using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Bookings;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Bookings.BookingWorld;
using static CarRental.Integration.Tests.Messaging.WhatsAppTestData;

namespace CarRental.Integration.Tests.Messaging;

/// <summary>
/// Booking notifications end to end on SQL Server: a booking confirmed by phone publishes its status change, the outbox
/// sends the approved template to the main driver through the fake — only with his consent, once, and only for the
/// agency the platform's number serves (ADR-0067: the demo agency on the test host), so that the customer's reply comes
/// back to it; the conversation and the notification record belong to the agency of the file.
/// </summary>
public sealed class WhatsAppNotificationTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_confirmed_booking_sends_the_confirmation_template_to_a_consenting_driver_whose_reply_comes_back()
    {
        // The agency served by the number of the host (WhatsApp:AgencySlug), on days no other test books in it.
        var agency = await DemoAgencyAsync(host, Ct);
        var driver = await NewDriverAsync(host, agency.AgencyId, Ct);
        await ConsentAsync(driver);
        var waId = await WaIdOfAsync(driver);

        var booking = await BookingWorld.CreateAsync(host, Quick(agency, driver, InDays(95), InDays(98)), Ct);

        var sent = await WaitForAsync(() => Task.FromResult(host.Factory.WhatsApp.SentTo(waId)), list => list.Count > 0, "the confirmation", Ct);
        var template = sent.ShouldHaveSingleItem().Content.ShouldBeOfType<TemplateContent>();
        template.Name.ShouldBe("booking_confirmed");
        template.Language.ShouldBe("fr");
        template.Parameters[1].ShouldBe(booking.Number);
        template.Parameters[3].ShouldBe(await LocationNameAsync(agency.AgencyId, agency.OfficeId));

        var conversation = (await ConversationAsync(host, waId, Ct)).ShouldNotBeNull();
        conversation.PersonId.ShouldBe(driver);
        conversation.BookingId.ShouldBe(booking.BookingId);
        conversation.AgencyId.ShouldBe(agency.AgencyId, "the conversation of a notification belongs to the agency of the file");
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            var notification = await db.Set<BookingNotification>().AsNoTracking().SingleAsync(n => n.BookingId == booking.BookingId, Ct);
            notification.Kind.ShouldBe(NotificationKind.BookingConfirmed);
            notification.AgencyId.ShouldBe(agency.AgencyId);
            var message = await db.Set<Message>().AsNoTracking().SingleAsync(m => m.ConversationId == conversation.Id, Ct);
            message.Author.ShouldBe(MessageAuthor.System);
            message.MetaMessageId.ShouldBe(sent[0].MetaMessageId);
            message.AgencyId.ShouldBe(agency.AgencyId);
        }

        // The driver answers the notification: the reply reaches the same conversation, in the agency that notified him.
        using var client = host.CreateClient();
        (await PostAsync(client, Text(waId, "Merci, à bientôt !"), Ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var messages = await WaitForAsync(() => MessagesAsync(host, conversation.Id, Ct), list => list.Any(m => m.Direction == MessageDirection.Inbound), "the reply", Ct);
        messages.ShouldAllBe(m => m.AgencyId == agency.AgencyId);
        (await ConversationAsync(host, waId, Ct))!.Id.ShouldBe(conversation.Id);
    }

    [Fact]
    public async Task Without_the_consent_of_the_driver_no_notification_is_sent()
    {
        var agency = await DemoAgencyAsync(host, Ct);
        var driver = await NewDriverAsync(host, agency.AgencyId, Ct);
        var waId = await WaIdOfAsync(driver);

        var booking = await BookingWorld.CreateAsync(host, Quick(agency, driver, InDays(100), InDays(103)), Ct);

        // Once the status change of the file has been handled, nothing was queued for this number.
        await WaitForAsync(() => StatusChangeHandledAsync(booking.BookingId), handled => handled, "the status change handled", Ct);
        host.Factory.WhatsApp.SentTo(waId).ShouldBeEmpty();
        (await ConversationAsync(host, waId, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task An_agency_without_the_whatsapp_module_notifies_nobody_even_a_consenting_driver()
    {
        // A new agency: its WhatsApp module is off until the support switches it on.
        var agency = await BookingWorld.CreateAsync(host, Ct);
        var driver = await NewDriverAsync(host, agency.AgencyId, Ct);
        await ConsentAsync(driver);
        var waId = await WaIdOfAsync(driver);

        var booking = await BookingWorld.CreateAsync(host, Quick(agency, driver, InDays(12), InDays(15)), Ct);

        await WaitForAsync(() => StatusChangeHandledAsync(booking.BookingId), handled => handled, "the status change handled", Ct);
        host.Factory.WhatsApp.SentTo(waId).ShouldBeEmpty();
        (await ConversationAsync(host, waId, Ct)).ShouldBeNull();
        await using var scope = host.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<BookingNotification>().AnyAsync(n => n.BookingId == booking.BookingId, Ct))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task An_agency_with_the_module_on_that_the_number_does_not_serve_notifies_nobody()
    {
        // Its module on, but the number of the host serves the demo agency: its customer's reply would reach the demo agency.
        var agency = await BookingWorld.CreateAsync(host, Ct);
        await TestModules.EnableAsync(host.Factory.Services, agency.AgencyId, AgencyModules.WhatsApp, Ct);
        var driver = await NewDriverAsync(host, agency.AgencyId, Ct);
        await ConsentAsync(driver);
        var waId = await WaIdOfAsync(driver);

        var booking = await BookingWorld.CreateAsync(host, Quick(agency, driver, InDays(12), InDays(15)), Ct);

        await WaitForAsync(() => StatusChangeHandledAsync(booking.BookingId), handled => handled, "the status change handled", Ct);
        host.Factory.WhatsApp.SentTo(waId).ShouldBeEmpty();
        (await ConversationAsync(host, waId, Ct)).ShouldBeNull("nothing is recorded for it");
        await using var scope = host.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<BookingNotification>().AnyAsync(n => n.BookingId == booking.BookingId, Ct))
            .ShouldBeFalse();
        (await scope.ServiceProvider.GetRequiredService<IWhatsAppNumber>().ServesAsync(agency.AgencyId, Ct)).ShouldBeFalse();
    }

    private async Task<string> LocationNameAsync(Guid agencyId, Guid locationId)
    {
        await using var scope = host.CreateScope();
        var locations = await scope.ServiceProvider.GetRequiredService<IReferentialQueries>().GetLocationsAsync(agencyId, Ct);
        return locations.Single(l => l.Id == locationId).Name;
    }

    private async Task<bool> StatusChangeHandledAsync(Guid bookingId)
    {
        await using var scope = host.CreateScope();
        var id = bookingId.ToString();
        var pending = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<OutboxMessage>()
            .AsNoTracking()
            .Where(m => m.Type == typeof(BookingStatusChanged).FullName && m.Payload.Contains(id))
            .Select(m => m.ProcessedAtUtc)
            .ToListAsync(Ct);
        return pending.Count > 0 && pending.All(processed => processed is not null);
    }

    private async Task ConsentAsync(Guid personId)
    {
        await using var scope = host.CreateScope(BookingWorld.Agent);
        (await scope.ServiceProvider.GetRequiredService<ICustomerRegistration>()
            .SetConsentAsync(personId, ConsentPurposeInfo.WhatsAppNotifications, ConsentChannelInfo.Phone, true, Ct)).IsSuccess.ShouldBeTrue();
    }

    private async Task<string> WaIdOfAsync(Guid personId)
    {
        await using var scope = host.CreateScope(BookingWorld.Agent);
        var person = await scope.ServiceProvider.GetRequiredService<ICustomersQueries>().GetPersonAsync(personId, Ct);
        return person!.Phone!.TrimStart('+');
    }
}
