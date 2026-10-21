using System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using System.Text.RegularExpressions;
using CarRental.Domain.Common;

namespace CarRental.Domain.Messaging;

/// <summary>
/// The content of a message. Outbound contents are validated against the limits of the WhatsApp Cloud API (text,
/// reply buttons, list, template); inbound ones are kept as received (truncated to the same limits). A media is never
/// downloaded: only its kind and Meta id are kept (invariant 9: no identity document image travels through WhatsApp).
/// </summary>
/// <remarks>
/// <see cref="ToString"/> prints the kind only, so that a content never reaches a log by accident.
/// [NotMapped] (BCL annotation): a value stored in one column of its message (JSON), never an entity of its own. A
/// stored content is rebuilt through the <c>Restore</c> factories, never validated again: it was valid by the rules of
/// its time (or received as is), and a message must stay readable when the rules change.
/// </remarks>
[NotMapped]
public abstract record MessageContent
{
    private protected MessageContent()
    {
    }

    public abstract MessageKind Kind { get; }

    /// <summary>
    /// Readable text of the message for the thread, the previews and the bot history: the text body, the body of an
    /// interactive message, the title of a chosen reply, the parameters of a template; <see langword="null"/> for a media.
    /// </summary>
    public abstract string? Text { get; }

    /// <summary>A free-form message (text or interactive), allowed only within the 24-hour customer service window.</summary>
    public bool IsFreeForm => this is TextContent or ButtonsContent or ListContent;

    public sealed override string ToString() => Kind.ToString();
}

/// <summary>A plain text message (WhatsApp: up to 4096 characters).</summary>
public sealed record TextContent : MessageContent
{
    public const int MaxLength = 4096;

    private TextContent(string body) => Body = body;

    public string Body { get; private init; }

    public override MessageKind Kind => MessageKind.Text;

    public override string? Text => Body;

    /// <summary>An outbound text: trimmed, line breaks normalized, 1 to <see cref="MaxLength"/> characters.</summary>
    public static Result<TextContent> Create(string? body)
    {
        var cleaned = MessageTexts.Clean(body);
        return cleaned is null || cleaned.Length > MaxLength ? MessagingErrors.TextInvalid : new TextContent(cleaned);
    }

    /// <summary>An inbound text, as received (possibly blank: a message of spaces only).</summary>
    public static TextContent Received(string? body) => new(MessageTexts.Truncate(MessageTexts.Clean(body) ?? string.Empty, MaxLength));

    /// <summary>A text read back from storage, as it was written (never validated again).</summary>
    public static TextContent Restore(string? body) => new(body ?? string.Empty);
}

/// <summary>A reply button of an interactive message: the id comes back in the customer's reply, the title is shown.</summary>
public sealed record ReplyButton
{
    public const int IdMaxLength = 256;
    public const int TitleMaxLength = 20;

    private ReplyButton(string id, string title)
    {
        Id = id;
        Title = title;
    }

    public string Id { get; private init; }

    public string Title { get; private init; }

    public static Result<ReplyButton> Create(string? id, string? title)
    {
        var cleanId = MessageTexts.SingleLine(id);
        var cleanTitle = MessageTexts.SingleLine(title);
        return cleanId is null || cleanId.Length > IdMaxLength || cleanTitle is null || cleanTitle.Length > TitleMaxLength
            ? MessagingErrors.ButtonsInvalid
            : new ReplyButton(cleanId, cleanTitle);
    }

    /// <summary>A button read back from storage, as it was written (never validated again).</summary>
    public static ReplyButton Restore(string? id, string? title) => new(id ?? string.Empty, title ?? string.Empty);
}

/// <summary>An interactive message with 1 to 3 reply buttons (one tap answers: fewer round trips, SPEC §11).</summary>
public sealed record ButtonsContent : MessageContent
{
    public const int BodyMaxLength = 1024;
    public const int MaxButtons = 3;

    private ButtonsContent(string body, IReadOnlyList<ReplyButton> buttons, string? header, string? footer)
    {
        Body = body;
        Buttons = buttons;
        Header = header;
        Footer = footer;
    }

    public string Body { get; private init; }

    public IReadOnlyList<ReplyButton> Buttons { get; private init; }

    /// <summary>Optional text header.</summary>
    public string? Header { get; private init; }

    public string? Footer { get; private init; }

    public override MessageKind Kind => MessageKind.Interactive;

    public override string? Text => Body;

    public static Result<ButtonsContent> Create(string? body, IReadOnlyList<ReplyButton> buttons, string? header = null, string? footer = null)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        var cleanBody = MessageTexts.Clean(body);
        if (cleanBody is null || cleanBody.Length > BodyMaxLength
            || buttons.Count is 0 or > MaxButtons
            || buttons.Any(b => b is null)
            || buttons.Select(b => b.Id).Distinct(StringComparer.Ordinal).Count() != buttons.Count
            || buttons.Select(b => b.Title).Distinct(StringComparer.Ordinal).Count() != buttons.Count)
        {
            return MessagingErrors.ButtonsInvalid;
        }

        var texts = InteractiveTexts.Create(header, footer);
        return texts.IsFailure
            ? texts.Error
            : new ButtonsContent(cleanBody, [.. buttons], texts.Value.Header, texts.Value.Footer);
    }

    /// <summary>An interactive message read back from storage, as it was written (never validated again).</summary>
    public static ButtonsContent Restore(string? body, IEnumerable<ReplyButton> buttons, string? header, string? footer)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        return new ButtonsContent(body ?? string.Empty, [.. buttons], header, footer);
    }
}

/// <summary>A row of an interactive list.</summary>
public sealed record ListRow
{
    public const int IdMaxLength = 200;
    public const int TitleMaxLength = 24;
    public const int DescriptionMaxLength = 72;

    private ListRow(string id, string title, string? description)
    {
        Id = id;
        Title = title;
        Description = description;
    }

    public string Id { get; private init; }

    public string Title { get; private init; }

    public string? Description { get; private init; }

    public static Result<ListRow> Create(string? id, string? title, string? description = null)
    {
        var cleanId = MessageTexts.SingleLine(id);
        var cleanTitle = MessageTexts.SingleLine(title);
        var cleanDescription = MessageTexts.SingleLine(description);
        return cleanId is null || cleanId.Length > IdMaxLength
               || cleanTitle is null || cleanTitle.Length > TitleMaxLength
               || cleanDescription?.Length > DescriptionMaxLength
            ? MessagingErrors.ListInvalid
            : new ListRow(cleanId, cleanTitle, cleanDescription);
    }

    /// <summary>A row read back from storage, as it was written (never validated again).</summary>
    public static ListRow Restore(string? id, string? title, string? description) => new(id ?? string.Empty, title ?? string.Empty, description);
}

/// <summary>A section of an interactive list; its title is required when the list has several sections.</summary>
public sealed record ListSection
{
    public const int TitleMaxLength = 24;

    private ListSection(string? title, IReadOnlyList<ListRow> rows)
    {
        Title = title;
        Rows = rows;
    }

    public string? Title { get; private init; }

    public IReadOnlyList<ListRow> Rows { get; private init; }

    public static Result<ListSection> Create(string? title, IReadOnlyList<ListRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var cleanTitle = MessageTexts.SingleLine(title);
        return rows.Count == 0 || rows.Any(r => r is null) || cleanTitle?.Length > TitleMaxLength
            ? MessagingErrors.ListInvalid
            : new ListSection(cleanTitle, [.. rows]);
    }

    /// <summary>A section read back from storage, as it was written (never validated again).</summary>
    public static ListSection Restore(string? title, IEnumerable<ListRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return new ListSection(title, [.. rows]);
    }
}

/// <summary>An interactive list: a button opens up to 10 rows in sections (choice of a category, a place…).</summary>
public sealed record ListContent : MessageContent
{
    public const int BodyMaxLength = 4096;
    public const int ButtonTextMaxLength = 20;
    public const int MaxSections = 10;
    public const int MaxRows = 10;

    private ListContent(string body, string buttonText, IReadOnlyList<ListSection> sections, string? header, string? footer)
    {
        Body = body;
        ButtonText = buttonText;
        Sections = sections;
        Header = header;
        Footer = footer;
    }

    public string Body { get; private init; }

    /// <summary>Text of the button that opens the list.</summary>
    public string ButtonText { get; private init; }

    public IReadOnlyList<ListSection> Sections { get; private init; }

    public string? Header { get; private init; }

    public string? Footer { get; private init; }

    public override MessageKind Kind => MessageKind.Interactive;

    public override string? Text => Body;

    public static Result<ListContent> Create(
        string? body,
        string? buttonText,
        IReadOnlyList<ListSection> sections,
        string? header = null,
        string? footer = null)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var cleanBody = MessageTexts.Clean(body);
        var cleanButton = MessageTexts.SingleLine(buttonText);
        var rows = sections.Where(s => s is not null).SelectMany(s => s.Rows).ToList();
        if (cleanBody is null || cleanBody.Length > BodyMaxLength
            || cleanButton is null || cleanButton.Length > ButtonTextMaxLength
            || sections.Count is 0 or > MaxSections
            || sections.Any(s => s is null || (sections.Count > 1 && s.Title is null))
            || rows.Count is 0 or > MaxRows
            || rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count)
        {
            return MessagingErrors.ListInvalid;
        }

        var texts = InteractiveTexts.Create(header, footer);
        return texts.IsFailure
            ? texts.Error
            : new ListContent(cleanBody, cleanButton, [.. sections], texts.Value.Header, texts.Value.Footer);
    }

    /// <summary>A list read back from storage, as it was written (never validated again).</summary>
    public static ListContent Restore(string? body, string? buttonText, IEnumerable<ListSection> sections, string? header, string? footer)
    {
        ArgumentNullException.ThrowIfNull(sections);
        return new ListContent(body ?? string.Empty, buttonText ?? string.Empty, [.. sections], header, footer);
    }
}

/// <summary>
/// An approved template (name and language as approved by Meta) with its positional body parameters (<c>{{1}}</c>,
/// <c>{{2}}</c>…): the only message allowed outside the 24-hour window. Its text lives at Meta; the thread shows its
/// parameters.
/// </summary>
public sealed partial record TemplateContent : MessageContent
{
    public const int NameMaxLength = 512;
    public const int LanguageMaxLength = 15;
    public const int MaxParameters = 10;
    public const int ParameterMaxLength = 300;

    private TemplateContent(string name, string language, IReadOnlyList<string> parameters)
    {
        Name = name;
        Language = language;
        Parameters = parameters;
    }

    /// <summary>Name of the approved template, e.g. <c>booking_confirmation</c>.</summary>
    public string Name { get; private init; }

    /// <summary>Language and locale code of the approved translation, e.g. <c>fr</c> or <c>en_US</c>.</summary>
    public string Language { get; private init; }

    /// <summary>Values of the body variables, in order.</summary>
    public IReadOnlyList<string> Parameters { get; private init; }

    public override MessageKind Kind => MessageKind.Template;

    public override string? Text => Parameters.Count == 0 ? null : string.Join(" · ", Parameters);

    /// <summary>
    /// A template message. Parameters are kept on one line (Meta refuses new lines, tabs and runs of spaces in them) and
    /// each must have 1 to <see cref="ParameterMaxLength"/> characters.
    /// </summary>
    public static Result<TemplateContent> Create(string? name, string? language, IReadOnlyList<string?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var cleanName = name?.Trim();
        var cleanLanguage = language?.Trim();
        if (cleanName is null || cleanName.Length > NameMaxLength || !NamePattern().IsMatch(cleanName)
            || cleanLanguage is null || cleanLanguage.Length > LanguageMaxLength || !LanguagePattern().IsMatch(cleanLanguage))
        {
            return MessagingErrors.TemplateInvalid;
        }

        if (parameters.Count > MaxParameters)
        {
            return MessagingErrors.TemplateParameterInvalid;
        }

        var values = new List<string>(parameters.Count);
        foreach (var parameter in parameters)
        {
            var cleaned = MessageTexts.SingleLine(parameter);
            if (cleaned is null || cleaned.Length > ParameterMaxLength)
            {
                return MessagingErrors.TemplateParameterInvalid;
            }

            values.Add(cleaned);
        }

        return new TemplateContent(cleanName, cleanLanguage, values);
    }

    /// <summary>A template message read back from storage, as it was written (never validated again).</summary>
    public static TemplateContent Restore(string? name, string? language, IEnumerable<string?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new TemplateContent(name ?? string.Empty, language ?? string.Empty, [.. parameters.Select(p => p ?? string.Empty)]);
    }

    [GeneratedRegex("^[a-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[a-z]{2,3}(_[A-Z]{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePattern();
}

/// <summary>
/// The customer chose a reply button (of an interactive message, or a quick reply button of a template) or a row of a
/// list: the id (or payload) that was sent with it, and the title shown to the customer.
/// </summary>
public sealed record ReplyContent : MessageContent
{
    public const int ReplyIdMaxLength = ReplyButton.IdMaxLength;
    public const int TitleMaxLength = 256;

    private ReplyContent(MessageKind kind, string replyId, string title, string? description)
    {
        Kind = kind;
        ReplyId = replyId;
        Title = title;
        Description = description;
    }

    public override MessageKind Kind { get; }

    public string ReplyId { get; private init; }

    public string Title { get; private init; }

    public string? Description { get; private init; }

    public override string? Text => Title;

    /// <summary>A reply button (interactive message) or a quick reply button (template) chosen by the customer.</summary>
    public static ReplyContent ButtonChoice(string? replyId, string? title) =>
        new(MessageKind.ButtonReply, Truncated(replyId, ReplyIdMaxLength), Truncated(title, TitleMaxLength), null);

    /// <summary>A row of an interactive list chosen by the customer.</summary>
    public static ReplyContent ListChoice(string? replyId, string? title, string? description) =>
        new(
            MessageKind.ListReply,
            Truncated(replyId, ReplyIdMaxLength),
            Truncated(title, TitleMaxLength),
            MessageTexts.SingleLine(description) is { } text ? MessageTexts.Truncate(text, ListRow.DescriptionMaxLength) : null);

    private static string Truncated(string? value, int maxLength) => MessageTexts.Truncate(MessageTexts.SingleLine(value) ?? string.Empty, maxLength);
}

/// <summary>An inbound media: only its kind and the Meta media id are kept, the file is never downloaded.</summary>
public sealed record MediaContent : MessageContent
{
    public const int MediaIdMaxLength = 128;

    private MediaContent(MediaKind mediaKind, string? mediaId)
    {
        MediaKind = mediaKind;
        MediaId = mediaId;
    }

    public MediaKind MediaKind { get; private init; }

    /// <summary>Id of the media at Meta (never used to download it).</summary>
    public string? MediaId { get; private init; }

    public override MessageKind Kind => MessageKind.Media;

    public override string? Text => null;

    public static MediaContent Received(MediaKind mediaKind, string? mediaId) =>
        new(mediaKind, MessageTexts.SingleLine(mediaId) is { } id ? MessageTexts.Truncate(id, MediaIdMaxLength) : null);
}

/// <summary>An inbound message of a type the platform does not handle (location, contacts, reaction…): only its type is kept.</summary>
public sealed record UnsupportedContent : MessageContent
{
    public const int TypeMaxLength = 40;

    private UnsupportedContent(string originalType) => OriginalType = originalType;

    /// <summary>The <c>type</c> reported by Meta, e.g. <c>location</c>.</summary>
    public string OriginalType { get; private init; }

    public override MessageKind Kind => MessageKind.Unsupported;

    public override string? Text => null;

    public static UnsupportedContent Received(string? originalType) =>
        new(MessageTexts.Truncate(MessageTexts.SingleLine(originalType) ?? "unknown", TypeMaxLength));
}

/// <summary>Optional header and footer texts of an interactive message.</summary>
internal sealed record InteractiveTexts(string? Header, string? Footer)
{
    public const int HeaderFooterMaxLength = 60;

    public static Result<InteractiveTexts> Create(string? header, string? footer)
    {
        var cleanHeader = MessageTexts.SingleLine(header);
        var cleanFooter = MessageTexts.SingleLine(footer);
        return cleanHeader?.Length > HeaderFooterMaxLength || cleanFooter?.Length > HeaderFooterMaxLength
            ? MessagingErrors.HeaderOrFooterInvalid
            : new InteractiveTexts(cleanHeader, cleanFooter);
    }
}

/// <summary>Text cleaning shared by the message contents.</summary>
internal static class MessageTexts
{
    /// <summary>
    /// A multi-line text: line breaks normalized to <c>\n</c>, other control characters removed, trimmed;
    /// <see langword="null"/> when blank.
    /// </summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var character in normalized)
        {
            if (character == '\n' || character == '\t' || !char.IsControl(character))
            {
                builder.Append(character == '\t' ? ' ' : character);
            }
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>A one-line text: every run of white space (line breaks, tabs) becomes one space, trimmed; null when blank.</summary>
    public static string? SingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var space = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                space = true;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(character);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    public static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
