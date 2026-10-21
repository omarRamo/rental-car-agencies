using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Notifications;
using CarRental.Application.Tests.Messaging.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Tests.Messaging;

/// <summary>
/// Booking notifications: an approved template to the main driver, only with his WhatsApp notifications consent, once
/// per file and kind, in the conversation of his number (created and linked when needed).
/// </summary>
public sealed class NotificationTests : IAsyncDisposable
{
    private const string Phone = "+21622222002";

    private readonly MessagingHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private DateTimeOffset Tomorrow10 => TunisTime.FromLocal(TunisTime.Today(_harness.Clock).AddDays(1), new TimeOnly(10, 0));

    [Fact]
    public async Task A_confirmed_file_is_notified_with_its_template_and_parameters()
    {
        var personId = _harness.AddPerson(Phone, consent: true, firstName: "Amel");
        var booking = _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Confirmed, Tomorrow10, Tomorrow10.AddDays(4), personId);

        await _harness.HandleAsync(StatusChanged(booking, BookingStatusInfo.PendingValidation, BookingStatusInfo.Confirmed));
        await _harness.DrainAsync();

        var sent = _harness.WhatsApp.Sent.ShouldHaveSingleItem();
        sent.To.ShouldBe("21622222002");
        var template = sent.Content.ShouldBeOfType<TemplateContent>();
        template.Name.ShouldBe("booking_confirmed");
        template.Language.ShouldBe("fr");
        template.Parameters[0].ShouldBe("Amel");
        template.Parameters[1].ShouldBe(booking.Number);
        template.Parameters[2].ShouldContain("10:00");
        template.Parameters[3].ShouldBe("Agence Tunis centre");

        var conversation = _harness.Conversations.ShouldHaveSingleItem();
        conversation.WhatsAppNumber.ShouldBe(Phone);
        conversation.PersonId.ShouldBe(personId);
        conversation.BookingId.ShouldBe(booking.Id);
        var message = _harness.Messages.ShouldHaveSingleItem();
        message.Author.ShouldBe(MessageAuthor.System);
        message.Status.ShouldBe(MessageStatus.Accepted);
        _harness.Db.Rows<BookingNotification>().ShouldHaveSingleItem().Kind.ShouldBe(NotificationKind.BookingConfirmed);
    }

    [Fact]
    public async Task Without_the_consent_of_the_customer_nothing_is_sent()
    {
        var personId = _harness.AddPerson(Phone, consent: false);
        var booking = _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Confirmed, Tomorrow10, Tomorrow10.AddDays(4), personId);

        var outcome = await NotifyAsync(booking.Id, NotificationKind.BookingConfirmed);

        outcome.ShouldBe(NotificationOutcome.NoConsent);
        _harness.WhatsApp.Sent.ShouldBeEmpty();
        _harness.Conversations.ShouldBeEmpty();
        _harness.Db.Rows<BookingNotification>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_withdrawn_consent_stops_the_notifications()
    {
        var personId = _harness.AddPerson(Phone, consent: true);
        _harness.Customers.Consents.Add((personId, CarRental.Application.Customers.Contracts.ConsentPurposeInfo.WhatsAppNotifications, CarRental.Application.Customers.Contracts.ConsentChannelInfo.Email, false));
        var booking = _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Confirmed, Tomorrow10, Tomorrow10.AddDays(4), personId);

        (await NotifyAsync(booking.Id, NotificationKind.BookingConfirmed)).ShouldBe(NotificationOutcome.NoConsent);
    }

    [Fact]
    public async Task A_file_is_notified_once_per_kind_however_often_it_is_asked()
    {
        var personId = _harness.AddPerson(Phone, consent: true);
        var booking = _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Confirmed, Tomorrow10, Tomorrow10.AddDays(4), personId);

        (await NotifyAsync(booking.Id, NotificationKind.BookingConfirmed)).ShouldBe(NotificationOutcome.Queued);
        (await NotifyAsync(booking.Id, NotificationKind.BookingConfirmed)).ShouldBe(NotificationOutcome.AlreadySent);
        (await NotifyAsync(booking.Id, NotificationKind.PickupReminder)).ShouldBe(NotificationOutcome.Queued);
        await _harness.HandleAsync(StatusChanged(booking, BookingStatusInfo.PendingValidation, BookingStatusInfo.Confirmed));
        await _harness.DrainAsync();

        _harness.WhatsApp.Sent.Count.ShouldBe(2);
        _harness.Conversations.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(BookingStatusInfo.Confirmed, NotificationKind.PaymentLink)]
    [InlineData(BookingStatusInfo.Cancelled, NotificationKind.BookingConfirmed)]
    [InlineData(BookingStatusInfo.InProgress, NotificationKind.PickupReminder)]
    [InlineData(BookingStatusInfo.Confirmed, NotificationKind.ReturnReminder)]
    [InlineData(BookingStatusInfo.InProgress, NotificationKind.ReviewRequest)]
    public async Task A_notification_is_sent_only_while_the_file_is_in_its_status(BookingStatusInfo status, NotificationKind kind)
    {
        var personId = _harness.AddPerson(Phone, consent: true);
        var booking = _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, status, Tomorrow10, Tomorrow10.AddDays(4), personId);

        (await NotifyAsync(booking.Id, kind)).ShouldBe(NotificationOutcome.NotApplicable);
    }

    [Fact]
    public async Task No_main_driver_no_phone_or_an_anonymized_person_means_no_recipient()
    {
        var noDriver = _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Confirmed, Tomorrow10, Tomorrow10.AddDays(1), null);
        var withoutPhone = _harness.AddPerson(Phone, consent: true);
        _harness.Customers.Persons[withoutPhone] = _harness.Customers.Persons[withoutPhone] with { Phone = null };
        var anonymized = _harness.AddPerson(Phone, consent: true);
        _harness.Customers.Persons[anonymized] = _harness.Customers.Persons[anonymized] with { IsAnonymized = true };

        (await NotifyAsync(noDriver.Id, NotificationKind.BookingConfirmed)).ShouldBe(NotificationOutcome.NoRecipient);
        (await NotifyAsync(_harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Confirmed, Tomorrow10, Tomorrow10.AddDays(1), withoutPhone).Id, NotificationKind.BookingConfirmed))
            .ShouldBe(NotificationOutcome.NoRecipient);
        (await NotifyAsync(_harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Confirmed, Tomorrow10, Tomorrow10.AddDays(1), anonymized).Id, NotificationKind.BookingConfirmed))
            .ShouldBe(NotificationOutcome.NoRecipient);
    }

    [Fact]
    public async Task An_empty_template_name_or_whatsapp_not_configured_sends_nothing()
    {
        var personId = _harness.AddPerson(Phone, consent: true);
        var booking = _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Confirmed, Tomorrow10, Tomorrow10.AddDays(4), personId);
        _harness.Settings.Set(MessagingSettings.BookingConfirmedTemplate, string.Empty);

        (await NotifyAsync(booking.Id, NotificationKind.BookingConfirmed)).ShouldBe(NotificationOutcome.NotConfigured);

        _harness.WhatsApp.IsEnabled = false;
        (await NotifyAsync(booking.Id, NotificationKind.PickupReminder)).ShouldBe(NotificationOutcome.Disabled);
        _harness.Db.Rows<BookingNotification>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_validated_request_waiting_for_its_payment_gets_the_amount_and_the_deadline()
    {
        var personId = _harness.AddPerson(Phone, consent: true, firstName: "Karim");
        var due = _harness.Clock.GetUtcNow().AddHours(24);
        var booking = _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.AwaitingPayment, Tomorrow10, Tomorrow10.AddDays(4), personId, due);
        _harness.Settings.Set(MessagingSettings.TemplateLanguage, "en_US");

        await _harness.HandleAsync(StatusChanged(booking, BookingStatusInfo.PendingValidation, BookingStatusInfo.AwaitingPayment));
        await _harness.DrainAsync();

        var template = _harness.WhatsApp.Sent.ShouldHaveSingleItem().Content.ShouldBeOfType<TemplateContent>();
        template.Name.ShouldBe("payment_link");
        template.Language.ShouldBe("en_US");
        template.Parameters.ShouldBe(["Karim", booking.Number, "681.400 TND", TunisTime.ToTunis(due).ToString("f", System.Globalization.CultureInfo.GetCultureInfo("en-US"))]);
    }

    [Fact]
    public async Task Other_status_changes_notify_nothing()
    {
        var personId = _harness.AddPerson(Phone, consent: true);
        var booking = _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Confirmed, Tomorrow10, Tomorrow10.AddDays(4), personId);

        await _harness.HandleAsync(StatusChanged(booking, BookingStatusInfo.Confirmed, BookingStatusInfo.InProgress));
        await _harness.HandleAsync(StatusChanged(booking, BookingStatusInfo.Confirmed, BookingStatusInfo.Confirmed));
        await _harness.DrainAsync();

        _harness.WhatsApp.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_day_before_the_pick_up_the_reminder_carries_the_address()
    {
        var personId = _harness.AddPerson(Phone, consent: true, firstName: "Sami");
        var tomorrow = _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Confirmed, Tomorrow10, Tomorrow10.AddDays(3), personId);
        _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Confirmed, Tomorrow10.AddDays(2), Tomorrow10.AddDays(5), personId);
        _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.PendingValidation, Tomorrow10.AddHours(2), Tomorrow10.AddDays(5), personId);

        await RunJobAsync<PickupReminderJob>();
        await RunJobAsync<PickupReminderJob>();

        var template = _harness.WhatsApp.Sent.ShouldHaveSingleItem().Content.ShouldBeOfType<TemplateContent>();
        template.Name.ShouldBe("pickup_reminder");
        template.Parameters.ShouldBe(["Sami", tomorrow.Number, TunisTime.ToTunis(Tomorrow10).ToString("f", System.Globalization.CultureInfo.GetCultureInfo("fr")), "Agence Tunis centre", FakeAddressReferential.OfficeAddress]);
    }

    [Fact]
    public async Task The_day_before_the_return_a_rental_in_progress_gets_its_reminder()
    {
        var personId = _harness.AddPerson(Phone, consent: true);
        var inProgress = _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.InProgress, Tomorrow10.AddDays(-3), Tomorrow10.AddHours(8), personId);

        await RunJobAsync<ReturnReminderJob>();

        var template = _harness.WhatsApp.Sent.ShouldHaveSingleItem().Content.ShouldBeOfType<TemplateContent>();
        template.Name.ShouldBe("return_reminder");
        template.Parameters[1].ShouldBe(inProgress.Number);
        template.Parameters[4].ShouldBe(FakeAddressReferential.OfficeAddress);
    }

    [Fact]
    public async Task The_day_after_the_return_a_review_is_asked_when_a_review_link_is_set()
    {
        var personId = _harness.AddPerson(Phone, consent: true, firstName: "Nour");
        var yesterday = TunisTime.FromLocal(TunisTime.Today(_harness.Clock).AddDays(-1), new TimeOnly(18, 0));
        _harness.Bookings.Add(_harness.AgencyId, _harness.OfficeId, BookingStatusInfo.Returned, yesterday.AddDays(-5), yesterday, personId);

        await RunJobAsync<ReviewRequestJob>();
        _harness.WhatsApp.Sent.ShouldBeEmpty();

        _harness.Settings.Set(MessagingSettings.ReviewLink, "https://g.page/r/agence-demo/review");
        await RunJobAsync<ReviewRequestJob>();

        _harness.WhatsApp.Sent.ShouldHaveSingleItem().Content.ShouldBeOfType<TemplateContent>().Parameters
            .ShouldBe(["Nour", "https://g.page/r/agence-demo/review"]);
    }

    private static BookingStatusChanged StatusChanged(BookingSummaryInfo booking, BookingStatusInfo? previous, BookingStatusInfo status) =>
        new(booking.Id, booking.AgencyId, booking.Number, booking.Channel, previous, status, booking.TotalInclVat, booking.PaymentDueAt, DateTimeOffset.UtcNow);

    private async Task<NotificationOutcome> NotifyAsync(Guid bookingId, NotificationKind kind)
    {
        var outcome = await _harness.RunAsync<BookingNotifier, NotificationOutcome>(n => n.NotifyAsync(bookingId, kind, TestContext.Current.CancellationToken));
        await _harness.DrainAsync();
        return outcome;
    }

    private async Task RunJobAsync<TJob>()
        where TJob : IRecurringJob
    {
        await using (var scope = _harness.Scope())
        {
            await scope.ServiceProvider.GetRequiredService<TJob>().ExecuteAsync(TestContext.Current.CancellationToken);
        }

        await _harness.DrainAsync();
    }
}
