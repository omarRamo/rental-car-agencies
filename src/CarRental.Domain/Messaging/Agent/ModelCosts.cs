namespace CarRental.Domain.Messaging.Agent;

/// <summary>
/// Tokens of one or more model calls, as the provider reports them: input tokens billed at full price (the uncached
/// remainder), output tokens (thinking included), tokens read from the prompt cache and tokens written to it.
/// </summary>
public sealed record ModelTokenUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public static ModelTokenUsage Zero { get; } = new(0, 0, 0, 0);

    /// <summary>Every token processed: input (uncached, read and written) and output.</summary>
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>Share of the prompt served from the cache (0 when no prompt was sent).</summary>
    public decimal CacheHitRatio
    {
        get
        {
            var prompt = InputTokens + CacheReadTokens + CacheWriteTokens;
            return prompt == 0 ? 0m : Math.Round((decimal)CacheReadTokens / prompt, 4, MidpointRounding.AwayFromZero);
        }
    }

    public ModelTokenUsage Add(ModelTokenUsage other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new ModelTokenUsage(
            checked(InputTokens + other.InputTokens),
            checked(OutputTokens + other.OutputTokens),
            checked(CacheReadTokens + other.CacheReadTokens),
            checked(CacheWriteTokens + other.CacheWriteTokens));
    }
}

/// <summary>
/// Prices of the model in USD per million tokens (business settings, platform-only): input, output, and the cache read
/// and write prices as ratios of the input price (0.1 and 1.25 for a 5-minute cache at the time of writing).
/// </summary>
public sealed record ModelPriceGrid(decimal InputUsdPerMillion, decimal OutputUsdPerMillion, decimal CacheReadRatio, decimal CacheWriteRatio)
{
    private const decimal Million = 1_000_000m;

    /// <summary>Cost of <paramref name="usage"/> in USD, to the millionth of a dollar (away from zero).</summary>
    public decimal CostUsd(ModelTokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var cost = (usage.InputTokens * InputUsdPerMillion
                    + usage.OutputTokens * OutputUsdPerMillion
                    + usage.CacheReadTokens * InputUsdPerMillion * CacheReadRatio
                    + usage.CacheWriteTokens * InputUsdPerMillion * CacheWriteRatio) / Million;
        return Math.Round(cost, 6, MidpointRounding.AwayFromZero);
    }
}

/// <summary>What stops the bot on a conversation: the caps of the settings (PROMPT §9.2 « plafonds de tours et de jetons »).</summary>
public sealed record AgentCaps(
    int MaxBotTurns,
    long MaxTokens,
    decimal MaxCostUsd,
    int MaxModelCallsPerTurn,
    int MaxToolCallsPerTurn,
    int MaxStatusLookups,
    int MaxTurnsWithoutProgress)
{
    /// <summary>
    /// The cap a conversation reached before a new turn, from its counters so far: turns of the bot, tokens, AI and Meta
    /// costs, consecutive turns without progress; <see cref="AgentCap.None"/> when the bot may answer.
    /// </summary>
    public AgentCap Reached(int botTurns, long tokens, decimal costUsd, int turnsWithoutProgress) => this switch
    {
        _ when botTurns >= MaxBotTurns => AgentCap.BotTurns,
        _ when tokens >= MaxTokens => AgentCap.Tokens,
        _ when costUsd >= MaxCostUsd => AgentCap.Cost,
        _ when turnsWithoutProgress >= MaxTurnsWithoutProgress => AgentCap.TurnsWithoutProgress,
        _ => AgentCap.None,
    };
}

/// <summary>A cap of the bot reached by a conversation or a turn.</summary>
public enum AgentCap
{
    None = 0,
    BotTurns = 1,
    Tokens = 2,
    Cost = 3,
    TurnsWithoutProgress = 4,
    ModelCallsPerTurn = 5,
    ToolCallsPerTurn = 6,
}
