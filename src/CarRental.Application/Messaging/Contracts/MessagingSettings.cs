using System.Text.RegularExpressions;
using CarRental.Application.Referential.Contracts;

namespace CarRental.Application.Messaging.Contracts;

/// <summary>
/// Business settings of the Messaging module, declared to the Referential catalogue by <c>AddMessagingModule()</c> and
/// edited in <c>/admin/settings</c>. The templates are the agency's approved WhatsApp templates (name and language as
/// approved by Meta): TODO(spec) the agency's approved template names and languages; the defaults are the names
/// documented in ADR-0048, with the parameters the platform fills. An empty name turns the notification off.
/// </summary>
public static partial class MessagingSettings
{
    private const int TemplateNameMaxLength = 512;

    /// <summary>Conversations and messages are purged after this many days without activity (365, SPEC §11 "12 mois par défaut").</summary>
    public static readonly SettingDefinition<int> RetentionDays =
        SettingDefinitions.WholeNumber("messaging.retention_days", 365, 30, 3650).PlatformOnly();

    /// <summary>Language and locale code of the approved templates (<c>fr</c>, <c>en</c>, <c>en_US</c>…); also the language of their parameters.</summary>
    public static readonly SettingDefinition<string> TemplateLanguage = new SettingDefinition<string>(
        "messaging.template_language",
        "fr",
        value => LanguagePattern().IsMatch(value) ? value : SettingErrors.InvalidValue("messaging.template_language"),
        value => value,
        new SettingEditor(SettingKind.Text, MaxLength: 15)).PlatformOnly();

    /// <summary>Confirmation of a rental file: {{1}} first name, {{2}} file number, {{3}} pick-up date and time, {{4}} pick-up place.</summary>
    public static readonly SettingDefinition<string> BookingConfirmedTemplate = TemplateName("messaging.templates.booking_confirmed", "booking_confirmed");

    /// <summary>Validated request waiting for its payment: {{1}} first name, {{2}} file number, {{3}} amount due, {{4}} payment deadline.</summary>
    public static readonly SettingDefinition<string> PaymentLinkTemplate = TemplateName("messaging.templates.payment_link", "payment_link");

    /// <summary>The day before the pick-up: {{1}} first name, {{2}} file number, {{3}} pick-up date and time, {{4}} place, {{5}} address.</summary>
    public static readonly SettingDefinition<string> PickupReminderTemplate = TemplateName("messaging.templates.pickup_reminder", "pickup_reminder");

    /// <summary>The day before the return: {{1}} first name, {{2}} file number, {{3}} return date and time, {{4}} place, {{5}} address.</summary>
    public static readonly SettingDefinition<string> ReturnReminderTemplate = TemplateName("messaging.templates.return_reminder", "return_reminder");

    /// <summary>After the return: {{1}} first name, {{2}} review link.</summary>
    public static readonly SettingDefinition<string> ReviewRequestTemplate = TemplateName("messaging.templates.review_request", "review_request");

    /// <summary>Sent by an agent outside the 24-hour window to reopen the conversation: {{1}} customer name.</summary>
    public static readonly SettingDefinition<string> FollowUpTemplate = TemplateName("messaging.templates.follow_up", "conversation_follow_up");

    /// <summary>Link sent with the review request (the agency's Google review page); empty: no review request.</summary>
    public static readonly SettingDefinition<string> ReviewLink = SettingDefinitions.Text("messaging.review_link", string.Empty, maxLength: 300);

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        RetentionDays,
        TemplateLanguage,
        BookingConfirmedTemplate,
        PaymentLinkTemplate,
        PickupReminderTemplate,
        ReturnReminderTemplate,
        ReviewRequestTemplate,
        FollowUpTemplate,
        ReviewLink,
    ];

    /// <summary>The name of an approved template (lower-case letters, digits and underscores), or empty to turn the message off.</summary>
    private static SettingDefinition<string> TemplateName(string key, string defaultName) => new SettingDefinition<string>(
        key,
        defaultName,
        value => value.Length == 0 || (value.Length <= TemplateNameMaxLength && TemplateNamePattern().IsMatch(value))
            ? value
            : SettingErrors.InvalidValue(key),
        value => value,
        new SettingEditor(SettingKind.Text, MaxLength: TemplateNameMaxLength)).PlatformOnly();

    [GeneratedRegex("^[a-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateNamePattern();

    [GeneratedRegex("^[a-z]{2,3}(_[A-Z]{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePattern();
}
