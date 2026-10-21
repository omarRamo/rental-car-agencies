using System.Globalization;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Sending;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging.Notifications;

/// <summary>What became of a notification request.</summary>
public enum NotificationOutcome
{
    /// <summary>The template message was queued for the customer.</summary>
    Queued = 1,

    /// <summary>This notification of this file was already queued (idempotence per file and kind).</summary>
    AlreadySent = 2,

    /// <summary>WhatsApp is not configured on this platform.</summary>
    Disabled = 3,

    /// <summary>No template configured for this kind (or no review link for a review request).</summary>
    NotConfigured = 4,

    /// <summary>The file does not exist or is no longer in a status this notification is about.</summary>
    NotApplicable = 5,

    /// <summary>No main driver, an anonymized person or no usable phone number.</summary>
    NoRecipient = 6,

    /// <summary>The main driver did not give the WhatsApp notifications consent.</summary>
    NoConsent = 7,

    /// <summary>The template could not be built (a parameter is missing or too long).</summary>
    Invalid = 8,
}

/// <summary>
/// Utility notifications of a rental file by WhatsApp (SPEC §11): an approved template sent to the main driver of the
/// file, only with his WhatsApp notifications consent (checked from the Customers module at the time of sending), at
/// most once per file and kind (<see cref="BookingNotification"/>, unique). The conversation of the number is created
/// when needed and linked to the person and the file; the template goes out through the outbox like any message.
/// </summary>
public sealed partial class BookingNotifier(
    IAppDbContext db,
    IBookingsQueries bookings,
    ICustomersQueries customers,
    IReferentialQueries referential,
    ISettingsReader settings,
    IWhatsAppClient client,
    TimeProvider clock,
    ILogger<BookingNotifier> logger)
{
    public async Task<NotificationOutcome> NotifyAsync(Guid bookingId, NotificationKind kind, CancellationToken cancellationToken)
    {
        var outcome = await TryNotifyAsync(bookingId, kind, cancellationToken);
        LogOutcome(kind.ToString(), outcome.ToString());
        return outcome;
    }

    /// <summary>Statuses of the file in which a notification of <paramref name="kind"/> still makes sense.</summary>
    internal static bool IsApplicable(NotificationKind kind, BookingStatusInfo status) => kind switch
    {
        NotificationKind.BookingConfirmed => status == BookingStatusInfo.Confirmed,
        NotificationKind.PaymentLink => status == BookingStatusInfo.AwaitingPayment,
        NotificationKind.PickupReminder => status == BookingStatusInfo.Confirmed,
        NotificationKind.ReturnReminder => status == BookingStatusInfo.InProgress,
        NotificationKind.ReviewRequest => status is BookingStatusInfo.Returned or BookingStatusInfo.Closed,
        _ => false,
    };

    /// <summary>The culture of the template parameters (dates, amounts): the template language, e.g. <c>en_US</c> → en-US.</summary>
    internal static CultureInfo CultureOf(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language.Replace('_', '-'));
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static SettingDefinition<string> TemplateSetting(NotificationKind kind) => kind switch
    {
        NotificationKind.BookingConfirmed => MessagingSettings.BookingConfirmedTemplate,
        NotificationKind.PaymentLink => MessagingSettings.PaymentLinkTemplate,
        NotificationKind.PickupReminder => MessagingSettings.PickupReminderTemplate,
        NotificationKind.ReturnReminder => MessagingSettings.ReturnReminderTemplate,
        NotificationKind.ReviewRequest => MessagingSettings.ReviewRequestTemplate,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown notification kind."),
    };

    private static string When(DateTimeOffset instant, CultureInfo culture) => TunisTime.ToTunis(instant).ToString("f", culture);

    private async Task<NotificationOutcome> TryNotifyAsync(Guid bookingId, NotificationKind kind, CancellationToken cancellationToken)
    {
        if (!client.IsEnabled)
        {
            return NotificationOutcome.Disabled;
        }

        var templateName = await settings.GetAsync(TemplateSetting(kind), cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(templateName))
        {
            return NotificationOutcome.NotConfigured;
        }

        if (await db.Set<BookingNotification>().AnyAsync(n => n.BookingId == bookingId && n.Kind == kind, cancellationToken))
        {
            return NotificationOutcome.AlreadySent;
        }

        var booking = await bookings.GetSummaryAsync(bookingId, cancellationToken);
        if (booking is null || !IsApplicable(kind, booking.Status))
        {
            return NotificationOutcome.NotApplicable;
        }

        if (booking.MainDriverId is not { } personId
            || await customers.GetPersonAsync(personId, cancellationToken) is not { IsAnonymized: false, Phone: { } phone } person
            || WhatsAppNumbers.Normalize(phone) is not { IsSuccess: true } number)
        {
            return NotificationOutcome.NoRecipient;
        }

        if (!await customers.HasConsentAsync(personId, ConsentPurposeInfo.WhatsAppNotifications, cancellationToken))
        {
            return NotificationOutcome.NoConsent;
        }

        var language = await settings.GetAsync(MessagingSettings.TemplateLanguage, cancellationToken: cancellationToken);
        var parameters = await ParametersAsync(kind, booking, person, CultureOf(language), cancellationToken);
        if (parameters is null)
        {
            return kind == NotificationKind.ReviewRequest ? NotificationOutcome.NotConfigured : NotificationOutcome.Invalid;
        }

        var template = TemplateContent.Create(templateName, language, parameters);
        if (template.IsFailure)
        {
            LogInvalidTemplate(kind.ToString(), template.Error.Code);
            return NotificationOutcome.Invalid;
        }

        var now = clock.GetUtcNow();
        var conversation = await db.Set<Conversation>().FirstOrDefaultAsync(c => c.WhatsAppNumber == number.Value, cancellationToken);
        if (conversation is null)
        {
            conversation = Conversation.Start(number.Value, profileName: null, now).Value;
            db.Set<Conversation>().Add(conversation);
        }

        if (conversation.PersonId is null)
        {
            conversation.LinkPerson(personId, (await customers.FindIndividualCustomerAsync(personId, cancellationToken))?.Id);
        }

        if (conversation.BookingId is null)
        {
            conversation.LinkBooking(booking.Id);
        }

        var message = conversation.Notify(template.Value, now);
        db.Set<Message>().Add(message);
        db.Set<BookingNotification>().Add(BookingNotification.Record(booking.Id, kind, conversation, message, personId, now));
        await db.SaveChangesAsync(cancellationToken);
        return NotificationOutcome.Queued;
    }

    /// <summary>The body parameters of the template of <paramref name="kind"/> (ADR-0048), or <see langword="null"/> when one is missing.</summary>
    private async Task<IReadOnlyList<string?>?> ParametersAsync(
        NotificationKind kind,
        BookingSummaryInfo booking,
        PersonInfo person,
        CultureInfo culture,
        CancellationToken cancellationToken)
    {
        var firstName = string.IsNullOrWhiteSpace(person.FirstName) ? person.FullName : person.FirstName;
        switch (kind)
        {
            case NotificationKind.BookingConfirmed:
                {
                    var place = await PlaceAsync(booking.AgencyId, booking.PickupLocationId, cancellationToken);
                    return [firstName, booking.Number, When(booking.PickupAt, culture), place.Name];
                }

            case NotificationKind.PaymentLink:
                return booking.PaymentDueAt is { } due
                    ? [firstName, booking.Number, string.Create(culture, $"{booking.TotalInclVat.Amount:N3} {booking.TotalInclVat.Currency.Code}"), When(due, culture)]
                    : null;

            case NotificationKind.PickupReminder:
                {
                    var place = await PlaceAsync(booking.AgencyId, booking.PickupLocationId, cancellationToken);
                    return [firstName, booking.Number, When(booking.PickupAt, culture), place.Name, place.Address];
                }

            case NotificationKind.ReturnReminder:
                {
                    var place = await PlaceAsync(booking.AgencyId, booking.ReturnLocationId, cancellationToken);
                    return [firstName, booking.Number, When(booking.ReturnAt, culture), place.Name, place.Address];
                }

            default:
                {
                    var link = await settings.GetAsync(MessagingSettings.ReviewLink, booking.AgencyId, cancellationToken);
                    return string.IsNullOrWhiteSpace(link) ? null : [firstName, link.Trim()];
                }
        }
    }

    /// <summary>Name and address of a place: the address of the location, else the agency's, else its city.</summary>
    private async Task<(string Name, string Address)> PlaceAsync(Guid agencyId, Guid locationId, CancellationToken cancellationToken)
    {
        var location = (await referential.GetLocationsAsync(agencyId, cancellationToken)).FirstOrDefault(l => l.Id == locationId);
        var agency = (await referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Id == agencyId);
        var name = location?.Name ?? agency?.Name ?? string.Empty;
        var address = new[] { location?.Address, agency?.Address, agency?.City, name }.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty;
        return (name, address);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WhatsApp notification {Kind}: {Outcome}.")]
    private partial void LogOutcome(string kind, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp notification {Kind}: the template could not be built ({ErrorCode}).")]
    private partial void LogInvalidTemplate(string kind, string errorCode);
}
