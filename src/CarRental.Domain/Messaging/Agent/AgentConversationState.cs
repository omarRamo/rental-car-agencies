using System.ComponentModel.DataAnnotations.Schema;
using CarRental.Domain.Common;

namespace CarRental.Domain.Messaging.Agent;

/// <summary>
/// What the WhatsApp agent remembers of a conversation between turns (its id is the conversation's): the options of its
/// latest availability search — so that <c>creer_lien_devis</c> takes an option by id, never free amounts —, the amounts
/// its tools returned (the amount guardrail accepts them in later replies), the booking status lookups (limited per
/// conversation), the consecutive turns without progress, and the last quote link sent. Ids, codes and amounts only:
/// no personal data. Deleted with its conversation, whose agency it carries.
/// </summary>
public sealed class AgentConversationState : AggregateRoot, IAgencyOwned
{
    /// <summary>Amounts kept for the guardrail (the most recent ones).</summary>
    public const int MaxRememberedAmounts = 80;

    public const int QuoteLinkMaxLength = 600;

    private List<AgentOption> _options = [];
    private List<decimal> _amounts = [];

    private AgentConversationState()
    {
    }

    private AgentConversationState(Guid conversationId, Guid agencyId, DateTimeOffset now)
        : base(conversationId)
    {
        AgencyId = agencyId;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    /// <summary>The conversation (same id).</summary>
    public Guid ConversationId => Id;

    /// <summary>The agency of the conversation.</summary>
    public Guid AgencyId { get; private set; }

    /// <summary>Options of the latest search, in the order they were proposed (three at most).</summary>
    public IReadOnlyList<AgentOption> Options => _options;

    /// <summary>Searches made so far: the letter of the option ids (<c>A1</c>, then <c>B1</c>…).</summary>
    public int SearchCount { get; private set; }

    public DateTimeOffset? OptionsAtUtc { get; private set; }

    /// <summary>Amounts returned by the tools of the conversation (absolute values, TND).</summary>
    public IReadOnlyList<decimal> KnownAmounts => _amounts;

    public int StatusLookups { get; private set; }

    public int TurnsWithoutProgress { get; private set; }

    public string? LastQuoteLink { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static AgentConversationState Start(Guid conversationId, Guid agencyId, DateTimeOffset now)
    {
        if (conversationId == Guid.Empty || agencyId == Guid.Empty)
        {
            throw new ArgumentException("The conversation and its agency are required.", nameof(conversationId));
        }

        return new AgentConversationState(conversationId, agencyId, now);
    }

    /// <summary>The prefix of the option ids of the next search (<c>A</c>, <c>B</c>… then again <c>A</c> after <c>Z</c>).</summary>
    public string NextOptionPrefix() => ((char)('A' + (SearchCount % 26))).ToString();

    /// <summary>Keeps the options of a new search (they replace the previous ones) and their amounts.</summary>
    public void RecordSearch(IReadOnlyList<AgentOption> options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = [.. options];
        SearchCount++;
        OptionsAtUtc = now;
        RememberAmounts(options.SelectMany(o => new[] { o.Total.Amount, o.Deposit.Amount }));
        UpdatedAtUtc = now;
    }

    /// <summary>The option of the latest search with this id (case ignored), or <see langword="null"/>.</summary>
    public AgentOption? FindOption(string? optionId) =>
        string.IsNullOrWhiteSpace(optionId)
            ? null
            : _options.FirstOrDefault(o => string.Equals(o.Id, optionId.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds amounts returned by a tool (zero is ignored; the oldest go beyond <see cref="MaxRememberedAmounts"/>).</summary>
    public void RememberAmounts(IEnumerable<decimal> amounts)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        foreach (var amount in amounts.Select(Math.Abs).Where(a => a > 0m))
        {
            _amounts.Remove(amount);
            _amounts.Add(amount);
        }

        if (_amounts.Count > MaxRememberedAmounts)
        {
            _amounts = _amounts[^MaxRememberedAmounts..];
        }
    }

    /// <summary>Counts a booking status lookup; <see langword="false"/> (and not counted) once <paramref name="max"/> were made.</summary>
    public bool TryRecordStatusLookup(int max)
    {
        if (StatusLookups >= max)
        {
            return false;
        }

        StatusLookups++;
        return true;
    }

    public void RecordQuoteLink(string link, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(link);
        LastQuoteLink = link.Length > QuoteLinkMaxLength ? null : link;
        UpdatedAtUtc = now;
    }

    /// <summary>End of a turn of the bot: a turn with progress (a useful tool call, a better known need) resets the count.</summary>
    public void CompleteTurn(bool progress, DateTimeOffset now)
    {
        TurnsWithoutProgress = progress ? 0 : TurnsWithoutProgress + 1;
        UpdatedAtUtc = now;
    }
}

/// <summary>
/// An option proposed by the availability search, as the quote link tool re-prices it: category, places, instants (UTC)
/// and what the engine priced (total TTC and deposit). <see cref="Id"/> is what the model passes (<c>A1</c>, <c>A2</c>).
/// </summary>
// [NotMapped] (BCL annotation): stored as JSON in the state of the conversation, never an entity of its own.
[NotMapped]
public sealed record AgentOption(
    string Id,
    Guid AgencyId,
    Guid CategoryId,
    string CategoryCode,
    string CategoryName,
    Guid PickupLocationId,
    string PickupLocationCode,
    Guid ReturnLocationId,
    string ReturnLocationCode,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    int Days,
    Money Total,
    Money Deposit);

/// <summary>
/// One turn of the WhatsApp agent, for the cost reports of its agency (SPEC §11 « coût Meta et IA », « taux de
/// transfert »): when, what it cost (model calls, tool calls, tokens, AI and estimated Meta costs in USD) and how it
/// ended. Ids and counts only, no content; kept after the conversation is purged (the conversation id then points to
/// nothing, the agency stays).
/// </summary>
public sealed class AgentTurnRecord : Entity, IAgencyOwned
{
    public const int ModelMaxLength = 100;

    private AgentTurnRecord()
    {
    }

    private AgentTurnRecord(Guid id, Guid conversationId, Guid agencyId, DateTimeOffset at, string? model, AgentTurnOutcome outcome)
        : base(id)
    {
        ConversationId = conversationId;
        AgencyId = agencyId;
        OccurredAtUtc = at;
        Model = model is { Length: > ModelMaxLength } ? model[..ModelMaxLength] : model;
        Outcome = outcome;
    }

    public Guid ConversationId { get; private set; }

    /// <summary>The agency of the conversation.</summary>
    public Guid AgencyId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>The model called (<see langword="null"/> when the turn needed none).</summary>
    public string? Model { get; private set; }

    public AgentTurnOutcome Outcome { get; private set; }

    public AgentLanguage Language { get; private set; }

    public int ModelCalls { get; private set; }

    public int ToolCalls { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public long CacheReadTokens { get; private set; }

    public long CacheWriteTokens { get; private set; }

    public decimal AiCostUsd { get; private set; }

    public decimal MetaCostUsd { get; private set; }

    public static AgentTurnRecord Create(
        Guid conversationId,
        Guid agencyId,
        DateTimeOffset at,
        string? model,
        AgentTurnOutcome outcome,
        AgentLanguage language,
        int modelCalls,
        int toolCalls,
        ModelTokenUsage usage,
        decimal aiCostUsd,
        decimal metaCostUsd)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentOutOfRangeException.ThrowIfNegative(modelCalls);
        ArgumentOutOfRangeException.ThrowIfNegative(toolCalls);
        ArgumentOutOfRangeException.ThrowIfNegative(aiCostUsd);
        ArgumentOutOfRangeException.ThrowIfNegative(metaCostUsd);
        if (agencyId == Guid.Empty)
        {
            throw new ArgumentException("A turn is recorded for the agency of its conversation.", nameof(agencyId));
        }

        return new AgentTurnRecord(NewId(), conversationId, agencyId, at, model, outcome)
        {
            Language = language,
            ModelCalls = modelCalls,
            ToolCalls = toolCalls,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CacheReadTokens = usage.CacheReadTokens,
            CacheWriteTokens = usage.CacheWriteTokens,
            AiCostUsd = aiCostUsd,
            MetaCostUsd = metaCostUsd,
        };
    }
}

/// <summary>How a turn of the WhatsApp agent ended.</summary>
public enum AgentTurnOutcome
{
    /// <summary>The model's reply passed the guardrails and was sent.</summary>
    Replied = 1,

    /// <summary>A turn of attachments only: the fixed reply pointing to the secure link (no model call).</summary>
    AttachmentReply = 2,

    /// <summary>The model called <c>transferer_a_un_agent</c>.</summary>
    TransferredByModel = 3,

    /// <summary>A guardrail blocked the reply (unknown amount, confirmation promise): handed over.</summary>
    BlockedByGuardrail = 4,

    /// <summary>The reply would have disclosed the instructions or asked for a document photo: replaced by a fixed reply.</summary>
    ReplacedByGuardrail = 5,

    /// <summary>A cap of the conversation or of the turn was reached: handed over.</summary>
    CapReached = 6,

    /// <summary>The model refused (<c>refusal</c>): handed over, no fallback model on Claude Haiku 5.5.</summary>
    Refused = 7,

    /// <summary>The reply was cut by the output limit (<c>max_tokens</c>): handed over, never sent truncated.</summary>
    Truncated = 8,

    /// <summary>A tool failed unexpectedly, or the model service refused the request: handed over.</summary>
    Failed = 9,

    /// <summary>The model service was unavailable: the turn is retried later.</summary>
    Retried = 10,
}

/// <summary>
/// Retries of the pending bot turn of a conversation (its id is the conversation's) when the responder failed (model
/// unavailable, timeout): attempts so far and when the next one is due (ADR-0054). Removed once a turn succeeds; after
/// the last attempt the conversation is handed over to a human, never dropped. It carries the agency of the conversation.
/// </summary>
public sealed class BotTurnRetry : Entity, IAgencyOwned
{
    private BotTurnRetry()
    {
    }

    private BotTurnRetry(Guid conversationId, Guid agencyId)
        : base(conversationId)
    {
        AgencyId = agencyId;
    }

    public Guid ConversationId => Id;

    public Guid AgencyId { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public DateTimeOffset? LastFailedAtUtc { get; private set; }

    public static BotTurnRetry For(Guid conversationId, Guid agencyId)
    {
        if (conversationId == Guid.Empty || agencyId == Guid.Empty)
        {
            throw new ArgumentException("The conversation and its agency are required.", nameof(conversationId));
        }

        return new BotTurnRetry(conversationId, agencyId);
    }

    /// <summary>One more failed attempt; the next one is due after <paramref name="delay"/>.</summary>
    public void Failed(DateTimeOffset now, TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        Attempts++;
        LastFailedAtUtc = now;
        NextAttemptAtUtc = now + delay;
    }
}
