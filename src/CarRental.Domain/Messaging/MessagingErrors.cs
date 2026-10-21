using CarRental.Domain.Common;

namespace CarRental.Domain.Messaging;

/// <summary>Business errors of the Messaging module (codes <c>messaging.*</c>, texts <c>Error.{code}</c> in the web resources).</summary>
public static class MessagingErrors
{
    // Conversations.
    public static readonly Error ConversationNotFound = Error.NotFound("messaging.conversation.not_found", "The conversation does not exist.");

    public static readonly Error NumberInvalid = Error.Validation(
        "messaging.conversation.number_invalid",
        $"A WhatsApp number is an international number of {WhatsAppNumbers.MinDigits} to {WhatsAppNumbers.MaxDigits} digits.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "messaging.conversation.concurrent_update",
        "The conversation changed meanwhile: reload it and try again.");

    public static readonly Error WindowClosed = Error.Conflict(
        "messaging.conversation.window_closed",
        "Free-form messages can only be sent within 24 hours of the customer's last message: send an approved template.");

    public static readonly Error BotSilenced = Error.Conflict(
        "messaging.conversation.bot_silenced",
        "An agent handles the conversation: the bot does not answer.");

    public static readonly Error NotTakenOver = Error.Conflict(
        "messaging.conversation.not_taken_over",
        "Take the conversation over before answering the customer.");

    public static readonly Error HandoverReasonInvalid = Error.Validation(
        "messaging.conversation.handover_reason_invalid",
        $"The reason of a hand-over has 1 to {Conversation.HandoverReasonMaxLength} characters.");

    // Message contents.
    public static readonly Error TextInvalid = Error.Validation(
        "messaging.message.text_invalid",
        $"A text message has 1 to {TextContent.MaxLength} characters.");

    public static readonly Error ButtonsInvalid = Error.Validation(
        "messaging.message.buttons_invalid",
        $"Reply buttons: a body of 1 to {ButtonsContent.BodyMaxLength} characters and 1 to {ButtonsContent.MaxButtons} buttons with unique ids "
        + $"(up to {ReplyButton.IdMaxLength} characters) and unique titles (1 to {ReplyButton.TitleMaxLength} characters).");

    public static readonly Error ListInvalid = Error.Validation(
        "messaging.message.list_invalid",
        $"A list: a body of 1 to {ListContent.BodyMaxLength} characters, a button text of 1 to {ListContent.ButtonTextMaxLength} characters "
        + $"and 1 to {ListContent.MaxRows} rows with unique ids, in sections that have a title when there are several.");

    public static readonly Error HeaderOrFooterInvalid = Error.Validation(
        "messaging.message.header_footer_invalid",
        $"The header and the footer of an interactive message have at most {InteractiveTexts.HeaderFooterMaxLength} characters.");

    public static readonly Error TemplateInvalid = Error.Validation(
        "messaging.message.template_invalid",
        "A template needs its approved name (lower-case letters, digits and underscores) and a language code such as fr or en_US.");

    public static readonly Error TemplateParameterInvalid = Error.Validation(
        "messaging.message.template_parameter_invalid",
        $"A template has at most {TemplateContent.MaxParameters} parameters of 1 to {TemplateContent.ParameterMaxLength} characters each.");

    public static readonly Error ContentNotAllowed = Error.Validation(
        "messaging.message.content_not_allowed",
        "This kind of message cannot be sent by this author (the bot answers with text or interactive messages only).");

    public static readonly Error MessageNotFound = Error.NotFound("messaging.message.not_found", "The message does not exist.");

    // Structured need of the bot.
    public static readonly Error NeedInvalid = Error.Validation(
        "messaging.need.invalid",
        "The need is invalid: places of at most 100 characters, a return after the pick-up, 1 to 50 passengers, 0 to 50 bags, a positive budget.");

    // Shared inbox.
    public static readonly Error Forbidden = Error.Forbidden("messaging.forbidden", "The current user may not use the WhatsApp inbox.");

    public static readonly Error PersonNotFound = Error.NotFound(
        "messaging.link.person_not_found",
        "The person does not exist or was anonymized.");

    public static readonly Error BookingNotFound = Error.NotFound("messaging.link.booking_not_found", "The rental file does not exist.");

    public static readonly Error TemplateNotConfigured = Error.Validation(
        "messaging.template.not_configured",
        "No approved template is configured for this message (settings messaging.templates.*).");

    public static readonly Error Disabled = Error.Failure(
        "messaging.disabled",
        "WhatsApp is not configured on this platform (WhatsApp:Mode): messages cannot be sent.");

    public static readonly Error WebhookUnreadable = Error.Validation(
        "messaging.webhook.unreadable",
        "The webhook payload is not the JSON envelope of the WhatsApp Cloud API.");

    // Delivery failures recorded on outbound messages.
    public static readonly Error SendRejected = Error.Failure(
        "messaging.send.rejected",
        "Meta refused the message (see the Meta error code).");

    public static readonly Error SendUnavailable = Error.Failure(
        "messaging.send.unavailable",
        "The WhatsApp service could not be reached after several attempts.");

    public static readonly Error SendOutcomeUnknown = Error.Failure(
        "messaging.send.outcome_unknown",
        "Meta did not answer in time: the message may have been sent; check with the customer before sending it again.");
}
