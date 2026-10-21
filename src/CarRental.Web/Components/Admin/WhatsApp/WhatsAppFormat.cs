using System.Globalization;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Inbox;
using CarRental.Domain.Common;
using CarRental.Web.Components.Admin.Shared;
using Microsoft.Extensions.Localization;

namespace CarRental.Web.Components.Admin.WhatsApp;

/// <summary>Display helpers of the WhatsApp inbox (current UI culture; instants in Tunis time).</summary>
internal static class WhatsAppFormat
{
    /// <summary>An E.164 number with a space after the country code of Tunisia (<c>+216 98 123 456</c>), as typed elsewhere.</summary>
    public static string Number(string e164)
    {
        ArgumentNullException.ThrowIfNull(e164);
        return e164.StartsWith("+216", StringComparison.Ordinal) && e164.Length == 12
            ? string.Create(CultureInfo.InvariantCulture, $"+216 {e164[4..6]} {e164[6..9]} {e164[9..]}")
            : e164;
    }

    public static string Instant(DateTimeOffset instant) => TunisTime.ToTunis(instant).ToString("g", CultureInfo.CurrentCulture);

    /// <summary>The time of a message: the hour today, else the date and hour.</summary>
    public static string When(DateTimeOffset instant, DateTimeOffset now)
    {
        var local = TunisTime.ToTunis(instant);
        return TunisTime.DateOf(instant) == TunisTime.DateOf(now)
            ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("g", CultureInfo.CurrentCulture);
    }

    /// <summary>A duration such as <c>3 h 05</c> or <c>45 min</c>.</summary>
    public static string Duration(IStringLocalizer localizer, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return duration.TotalHours >= 1
            ? localizer["Duration.HoursMinutes", (int)duration.TotalHours, duration.Minutes.ToString("D2", CultureInfo.InvariantCulture)]
            : localizer["Duration.Minutes", Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes))];
    }

    /// <summary>Who the conversation is with: the linked person, else the WhatsApp profile name, else the number.</summary>
    public static string Title(string? personName, string? profileName, string number) =>
        !string.IsNullOrWhiteSpace(personName) ? personName
        : !string.IsNullOrWhiteSpace(profileName) ? profileName
        : Number(number);

    /// <summary>The text of a message in the thread or a preview: its text, or what it is when it has none.</summary>
    public static string Body(IStringLocalizer localizer, MessageKindInfo kind, string? text, MediaKindInfo? media, string? unsupportedType, string? templateName)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return kind switch
        {
            MessageKindInfo.Media => localizer[$"Message.Media.{media ?? MediaKindInfo.Document}"],
            MessageKindInfo.Unsupported => localizer["Message.Unsupported", unsupportedType ?? "?"],
            MessageKindInfo.Template => (string.IsNullOrWhiteSpace(templateName), string.IsNullOrWhiteSpace(text)) switch
            {
                (true, true) => localizer["Message.TemplateUnnamed"],
                (true, false) => localizer["Message.TemplateUnnamedWith", text!],
                (false, true) => localizer["Message.Template", templateName!],
                _ => localizer["Message.TemplateWith", templateName!, text!],
            },
            _ => text ?? string.Empty,
        };
    }

    /// <summary>The delivery state of an outbound message, with the reason of a failure.</summary>
    public static string Status(IStringLocalizer localizer, IStringLocalizer shared, InboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(message);
        var state = localizer[$"Status.{message.Status}"].Value;
        if (message.Status is not (MessageStatusInfo.Failed or MessageStatusInfo.Cancelled) || message.FailureCode is null)
        {
            return state;
        }

        var reason = ErrorMessages.For(shared, new Error(message.FailureCode, message.FailureCode, ErrorKind.Failure));
        return message.MetaErrorCode is { } code
            ? localizer["Status.FailedWith", state, reason, code.ToString(CultureInfo.InvariantCulture), message.MetaErrorTitle ?? string.Empty]
            : localizer["Status.FailedBecause", state, reason];
    }

    public static string Author(IStringLocalizer localizer, InboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(message);
        return message.Author == MessageAuthorInfo.Agent && !string.IsNullOrWhiteSpace(message.AuthorName)
            ? localizer["Author.AgentNamed", message.AuthorName]
            : localizer[$"Author.{message.Author}"];
    }
}
