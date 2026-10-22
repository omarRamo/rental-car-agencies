using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Customers.Contracts;
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
/// sends the approved template to the main driver through the fake — only with his consent, once, and only for an agency
/// whose WhatsApp module is on (ADR-0067); the conversation and the notification record belong to the agency of the file.
/// </summary>
public sealed class WhatsAppNotificationTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_confirmed_booking_sends_the_confirmation_template_to_a_consenting_driver()
    {
        var agency = await AgencyWithWhatsAppAsync();
        var driver = await NewDriverAsync(host, Ct);
        await ConsentAsync(driver);
        var waId = await WaIdOfAsync(driver);

        var booking = await BookingWorld.CreateAsync(host, Quick(agency, driver, InDays(12), InDays(15)), Ct);

        var sent = await WaitForAsync(() => Task.FromResult(host.Factory.WhatsApp.SentTo(waId)), list => list.Count > 0, "the confirmation", Ct);
        var template = sent.ShouldHaveSingleItem().Content.ShouldBeOfType<TemplateContent>();
        template.Name.ShouldBe("booking_confirmed");
        template.Language.ShouldBe("fr");
        template.Parameters[1].ShouldBe(booking.Number);
        template.Parameters[3].ShouldBe("Agence");

        var conversation = (await ConversationAsync(host, waId, Ct)).ShouldNotBeNull();
        conversation.PersonId.ShouldBe(driver);
        conversation.BookingId.ShouldBe(booking.BookingId);
        conversation.AgencyId.ShouldBe(agency.AgencyId, "the conversation of a notification belongs to the agency of the file");
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var notification = await db.Set<BookingNotification>().AsNoTracking().SingleAsync(n => n.BookingId == booking.BookingId, Ct);
        notification.Kind.ShouldBe(NotificationKind.BookingConfirmed);
        notification.AgencyId.ShouldBe(agency.AgencyId);
        var message = await db.Set<Message>().AsNoTracking().SingleAsync(m => m.ConversationId == conversation.Id, Ct);
        message.Author.ShouldBe(MessageAuthor.System);
        message.MetaMessageId.ShouldBe(sent[0].MetaMessageId);
        message.AgencyId.ShouldBe(agency.AgencyId);
    }

    [Fact]
    public async Task Without_the_consent_of_the_driver_no_notification_is_sent()
    {
        var agency = await AgencyWithWhatsAppAsync();
        var driver = await NewDriverAsync(host, Ct);
        var waId = await WaIdOfAsync(driver);

        var booking = await BookingWorld.CreateAsync(host, Quick(agency, driver, InDays(12), InDays(15)), Ct);

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
        var driver = await NewDriverAsync(host, Ct);
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

    /// <summary>An agency of the test whose WhatsApp module is on (off by default).</summary>
    private async Task<BookingAgency> AgencyWithWhatsAppAsync()
    {
        var agency = await BookingWorld.CreateAsync(host, Ct);
        await TestModules.EnableAsync(host.Factory.Services, agency.AgencyId, AgencyModules.WhatsApp, Ct);
        return agency;
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
