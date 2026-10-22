using System.Text.RegularExpressions;
using CarRental.Domain.Common;

namespace CarRental.Domain.Messaging.Agent;

/// <summary>
/// An answer of the knowledge base of an agency's WhatsApp agent (tool <c>infos_agence</c>, SPEC §11 « base de
/// connaissances validée par le gérant »; each agency has its own): a topic (documents, caution, annulation…), a title,
/// keywords that help find it, and the answer in French and English (Arabic optional). The bot only ever reads
/// <b>validated, active</b> entries of the agency of the conversation: an edit of a validated answer sends it back to
/// draft until a manager validates it again, so that nothing unvalidated is ever said. Changes are audited and kept in a
/// temporal history.
/// </summary>
public sealed partial class KnowledgeEntry : AggregateRoot, IAgencyOwned
{
    public const int TopicKeyMaxLength = 50;
    public const int TitleMaxLength = 150;
    public const int KeywordsMaxLength = 300;
    public const int AnswerMaxLength = 2000;
    public const int ValidatorNameMaxLength = AuditActor.UserNameMaxLength;

    private KnowledgeEntry()
    {
    }

    private KnowledgeEntry(Guid id, Guid agencyId, string topicKey, KnowledgeContent content, DateTimeOffset now)
        : base(id)
    {
        AgencyId = agencyId;
        TopicKey = topicKey;
        Apply(content);
        IsActive = true;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public Guid AgencyId { get; private set; }

    /// <summary>Stable key of the topic, lower case with underscores (<c>documents</c>, <c>aller_simple</c>), unique within the agency.</summary>
    public string TopicKey { get; private set; } = null!;

    public string Title { get; private set; } = null!;

    /// <summary>Words that point to this topic in a question (comma-separated), in any language.</summary>
    public string? Keywords { get; private set; }

    public string AnswerFrench { get; private set; } = null!;

    public string AnswerEnglish { get; private set; } = null!;

    public string? AnswerArabic { get; private set; }

    /// <summary>Validated by a manager since its last change: only validated entries reach the bot.</summary>
    public bool IsValidated { get; private set; }

    public Guid? ValidatedByUserId { get; private set; }

    public string? ValidatedByName { get; private set; }

    public DateTimeOffset? ValidatedAtUtc { get; private set; }

    /// <summary>A deactivated entry is kept (history) but never given to the bot.</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Whether the bot may say it.</summary>
    public bool IsUsableByBot => IsActive && IsValidated;

    /// <summary>A new draft of the agency's knowledge base (not validated): the bot ignores it until a manager validates it.</summary>
    public static Result<KnowledgeEntry> CreateDraft(Guid agencyId, string? topicKey, KnowledgeContent content, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (agencyId == Guid.Empty)
        {
            throw new ArgumentException("An answer belongs to the knowledge base of an agency.", nameof(agencyId));
        }

        var key = topicKey?.Trim().ToLowerInvariant();
        if (key is null || key.Length > TopicKeyMaxLength || !TopicKeyPattern().IsMatch(key))
        {
            return KnowledgeErrors.TopicInvalid;
        }

        var entry = new KnowledgeEntry(NewId(), agencyId, key, content, now);
        entry.Raise(new KnowledgeEntryChanged(entry.Id, entry.TopicKey));
        return entry;
    }

    /// <summary>Changes the texts; a change of a validated entry sends it back to draft (validation cleared).</summary>
    public void Edit(KnowledgeContent content, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content == CurrentContent)
        {
            return;
        }

        Apply(content);
        ClearValidation();
        UpdatedAtUtc = now;
        Raise(new KnowledgeEntryChanged(Id, TopicKey));
    }

    /// <summary>
    /// A manager validates the current texts: from now on the bot may give them. Refused when an answer promises the
    /// confirmation of a booking (the bot never does: its replies would be blocked).
    /// </summary>
    public Result Validate(AuditActor manager, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (manager.UserId is not { } userId)
        {
            throw new ArgumentException("Only a user of the back office validates an answer.", nameof(manager));
        }

        if (new[] { AnswerFrench, AnswerEnglish, AnswerArabic }.Any(ConfirmationPromises.Contains))
        {
            return KnowledgeErrors.ConfirmationPromise;
        }

        IsValidated = true;
        ValidatedByUserId = userId;
        ValidatedByName = manager.UserName is { } name ? (name.Length > ValidatorNameMaxLength ? name[..ValidatorNameMaxLength] : name) : null;
        ValidatedAtUtc = now;
        UpdatedAtUtc = now;
        Raise(new KnowledgeEntryChanged(Id, TopicKey));
        return Result.Success();
    }

    public void Deactivate(DateTimeOffset now)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        UpdatedAtUtc = now;
        Raise(new KnowledgeEntryChanged(Id, TopicKey));
    }

    /// <summary>Reactivates the entry as it is (a validated entry stays validated: its texts did not change).</summary>
    public void Activate(DateTimeOffset now)
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        UpdatedAtUtc = now;
        Raise(new KnowledgeEntryChanged(Id, TopicKey));
    }

    /// <summary>The answer in <paramref name="language"/> (Arabic falls back to French when not written, darija reads French).</summary>
    public string AnswerFor(AgentLanguage language) => language switch
    {
        AgentLanguage.English => AnswerEnglish,
        AgentLanguage.Arabic => AnswerArabic ?? AnswerFrench,
        _ => AnswerFrench,
    };

    private KnowledgeContent CurrentContent => new(Title, Keywords, AnswerFrench, AnswerEnglish, AnswerArabic);

    private void Apply(KnowledgeContent content)
    {
        Title = content.Title;
        Keywords = content.Keywords;
        AnswerFrench = content.AnswerFrench;
        AnswerEnglish = content.AnswerEnglish;
        AnswerArabic = content.AnswerArabic;
    }

    private void ClearValidation()
    {
        IsValidated = false;
        ValidatedByUserId = null;
        ValidatedByName = null;
        ValidatedAtUtc = null;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{1,49}$", RegexOptions.CultureInvariant)]
    private static partial Regex TopicKeyPattern();
}

/// <summary>The texts of a knowledge entry: title, keywords, answers in French and English (required) and Arabic (optional).</summary>
public sealed record KnowledgeContent
{
    private KnowledgeContent()
    {
        Title = string.Empty;
        AnswerFrench = string.Empty;
        AnswerEnglish = string.Empty;
    }

    internal KnowledgeContent(string title, string? keywords, string answerFrench, string answerEnglish, string? answerArabic)
    {
        Title = title;
        Keywords = keywords;
        AnswerFrench = answerFrench;
        AnswerEnglish = answerEnglish;
        AnswerArabic = answerArabic;
    }

    public string Title { get; private init; }

    public string? Keywords { get; private init; }

    public string AnswerFrench { get; private init; }

    public string AnswerEnglish { get; private init; }

    public string? AnswerArabic { get; private init; }

    public static Result<KnowledgeContent> Create(string? title, string? keywords, string? answerFrench, string? answerEnglish, string? answerArabic)
    {
        var cleanTitle = SingleLine(title);
        var cleanKeywords = SingleLine(keywords);
        var french = Text(answerFrench);
        var english = Text(answerEnglish);
        var arabic = Text(answerArabic);
        if (cleanTitle is null || cleanTitle.Length > KnowledgeEntry.TitleMaxLength
            || cleanKeywords?.Length > KnowledgeEntry.KeywordsMaxLength
            || french is null || french.Length > KnowledgeEntry.AnswerMaxLength
            || english is null || english.Length > KnowledgeEntry.AnswerMaxLength
            || arabic?.Length > KnowledgeEntry.AnswerMaxLength)
        {
            return KnowledgeErrors.ContentInvalid;
        }

        return new KnowledgeContent(cleanTitle, cleanKeywords, french, english, arabic);
    }

    private static string? SingleLine(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n')
            .Select(line => string.Join(' ', line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)));
        var text = string.Join('\n', lines).Trim();
        return text.Length == 0 || text.Any(c => char.IsControl(c) && c != '\n') ? null : text;
    }
}

/// <summary>A knowledge entry was created or changed (texts, validation, activity).</summary>
public sealed record KnowledgeEntryChanged(Guid EntryId, string TopicKey) : IDomainEvent;

/// <summary>Business errors of the knowledge base of the WhatsApp agent (texts <c>Error.{code}</c> in the web resources).</summary>
public static class KnowledgeErrors
{
    public static readonly Error NotFound = Error.NotFound("messaging.knowledge.not_found", "The knowledge entry does not exist.");

    public static readonly Error TopicInvalid = Error.Validation(
        "messaging.knowledge.topic_invalid",
        $"A topic key has 2 to {KnowledgeEntry.TopicKeyMaxLength} lower-case letters, digits or underscores and starts with a letter.");

    public static readonly Error TopicTaken = Error.Conflict("messaging.knowledge.topic_taken", "Another entry already has this topic key.");

    public static readonly Error ContentInvalid = Error.Validation(
        "messaging.knowledge.content_invalid",
        $"An entry needs a title of 1 to {KnowledgeEntry.TitleMaxLength} characters and answers in French and English of 1 to {KnowledgeEntry.AnswerMaxLength} characters.");

    public static readonly Error ConfirmationPromise = Error.Validation(
        "messaging.knowledge.confirmation_promise",
        "An answer promises the confirmation of a booking: the bot never does (an agent validates the request). Rephrase it before validating.");

    public static readonly Error Forbidden = Error.Forbidden("messaging.knowledge.forbidden", "The current user may not manage the knowledge base of the WhatsApp agent.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "messaging.knowledge.concurrent_update",
        "The entry changed meanwhile: reload it and try again.");
}

/// <summary>Explicit audit events of the WhatsApp agent (dotted, lower case, never renamed).</summary>
public static class AgentAuditActions
{
    /// <summary>A manager validated an answer of the knowledge base: the bot may give it.</summary>
    public const string KnowledgeValidated = "messaging.knowledge_validated";
}
