using System.ComponentModel.DataAnnotations.Schema;

namespace CarRental.Domain.Messaging;

/// <summary>
/// What the auto-responder cost so far on a conversation (PROMPT §9.2 "journal du coût par conversation"): turns, tokens
/// (input, output, cache read and write), tool calls, AI cost and estimated Meta cost of its replies, in USD. Read by the
/// caps of the bot (package 4B) and shown in the shared inbox.
/// </summary>
// [ComplexType] (BCL annotation): a value stored in the columns of the conversation, never a table of its own.
[ComplexType]
public sealed record ConversationUsage
{
    private ConversationUsage(
        int botTurns,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        int toolCalls,
        decimal aiCostUsd,
        decimal metaCostUsd)
    {
        BotTurns = botTurns;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CacheReadTokens = cacheReadTokens;
        CacheWriteTokens = cacheWriteTokens;
        ToolCalls = toolCalls;
        AiCostUsd = aiCostUsd;
        MetaCostUsd = metaCostUsd;
    }

    /// <summary>Turns answered (or declined) by the responder with a model call.</summary>
    public int BotTurns { get; private init; }

    public long InputTokens { get; private init; }

    public long OutputTokens { get; private init; }

    public long CacheReadTokens { get; private init; }

    public long CacheWriteTokens { get; private init; }

    public int ToolCalls { get; private init; }

    /// <summary>Cost of the model calls, in USD (6 decimals).</summary>
    public decimal AiCostUsd { get; private init; }

    /// <summary>Estimated Meta cost of the bot's billable replies, in USD (6 decimals).</summary>
    public decimal MetaCostUsd { get; private init; }

    public static ConversationUsage Zero { get; } = new(0, 0, 0, 0, 0, 0, 0m, 0m);

    /// <summary>The counters plus one turn.</summary>
    public ConversationUsage Add(BotTurnUsage turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return new ConversationUsage(
            checked(BotTurns + 1),
            checked(InputTokens + turn.InputTokens),
            checked(OutputTokens + turn.OutputTokens),
            checked(CacheReadTokens + turn.CacheReadTokens),
            checked(CacheWriteTokens + turn.CacheWriteTokens),
            checked(ToolCalls + turn.ToolCalls),
            AiCostUsd + turn.AiCostUsd,
            MetaCostUsd + turn.MetaCostUsd);
    }
}

/// <summary>Usage of one turn of the auto-responder (counts and costs are never negative: a negative value is a bug).</summary>
public sealed record BotTurnUsage
{
    public BotTurnUsage(
        int inputTokens,
        int outputTokens,
        int cacheReadTokens = 0,
        int cacheWriteTokens = 0,
        int toolCalls = 0,
        decimal aiCostUsd = 0m,
        decimal metaCostUsd = 0m)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(outputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheReadTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheWriteTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(toolCalls);
        ArgumentOutOfRangeException.ThrowIfNegative(aiCostUsd);
        ArgumentOutOfRangeException.ThrowIfNegative(metaCostUsd);
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CacheReadTokens = cacheReadTokens;
        CacheWriteTokens = cacheWriteTokens;
        ToolCalls = toolCalls;
        AiCostUsd = aiCostUsd;
        MetaCostUsd = metaCostUsd;
    }

    public int InputTokens { get; }

    public int OutputTokens { get; }

    public int CacheReadTokens { get; }

    public int CacheWriteTokens { get; }

    public int ToolCalls { get; }

    public decimal AiCostUsd { get; }

    public decimal MetaCostUsd { get; }
}
