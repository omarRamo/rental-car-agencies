using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Messaging.Agent;

namespace CarRental.Application.Messaging.Agent;

/// <summary>
/// Business settings of the WhatsApp agent (group <c>ai</c> of <c>/admin/settings</c>, one value for the whole
/// platform): the on/off switch, the caps per conversation and per turn (PROMPT §9.2 « plafonds de tours et de jetons »),
/// and the price grid that values the tokens (USD per million tokens; to check on the provider's pricing page) plus the
/// estimated Meta cost of a billable reply.
/// </summary>
public static class AgentSettings
{
    /// <summary>The bot answers the customers (true); off, the customer messages wait for an agent in the shared inbox.</summary>
    public static readonly SettingDefinition<bool> Enabled = SettingDefinitions.Boolean("ai.enabled", true).PlatformOnly();

    /// <summary>Turns of the bot on a conversation before the hand-over to an agent (30).</summary>
    public static readonly SettingDefinition<int> MaxBotTurns = SettingDefinitions.WholeNumber("ai.max_bot_turns", 30, 1, 500).PlatformOnly();

    /// <summary>Tokens (input, output and cache) a conversation may use before the hand-over (300 000).</summary>
    public static readonly SettingDefinition<int> MaxTokens = SettingDefinitions.WholeNumber("ai.max_tokens", 300_000, 1_000, 10_000_000).PlatformOnly();

    /// <summary>AI and Meta cost of a conversation, in USD, before the hand-over (0.50).</summary>
    public static readonly SettingDefinition<decimal> MaxCostUsd = SettingDefinitions.Number("ai.max_cost_usd", 0.50m, 0.01m, 100m).PlatformOnly();

    /// <summary>Model calls in one turn (4): beyond, the turn is handed over.</summary>
    public static readonly SettingDefinition<int> MaxModelCallsPerTurn = SettingDefinitions.WholeNumber("ai.max_model_calls_per_turn", 4, 1, 10).PlatformOnly();

    /// <summary>Tool calls in one turn (6): beyond, the turn is handed over.</summary>
    public static readonly SettingDefinition<int> MaxToolCallsPerTurn = SettingDefinitions.WholeNumber("ai.max_tool_calls_per_turn", 6, 1, 20).PlatformOnly();

    /// <summary>Booking status lookups on a conversation (3): beyond, the tool answers that the limit is reached.</summary>
    public static readonly SettingDefinition<int> MaxStatusLookups = SettingDefinitions.WholeNumber("ai.max_status_lookups", 3, 1, 20).PlatformOnly();

    /// <summary>Consecutive turns without progress (no useful tool result, need unchanged) before the hand-over (6).</summary>
    public static readonly SettingDefinition<int> MaxTurnsWithoutProgress = SettingDefinitions.WholeNumber("ai.max_turns_without_progress", 6, 2, 50).PlatformOnly();

    /// <summary>Price of a million input tokens in USD (0.10 for Claude Haiku 5.5 up to 100 000 tokens of prompt; to check).</summary>
    public static readonly SettingDefinition<decimal> InputUsdPerMillion = SettingDefinitions.Number("ai.price_input_usd_per_mtok", 0.10m, 0m, 100m).PlatformOnly();

    /// <summary>Price of a million output tokens in USD (0.50 for Claude Haiku 5.5; to check).</summary>
    public static readonly SettingDefinition<decimal> OutputUsdPerMillion = SettingDefinitions.Number("ai.price_output_usd_per_mtok", 0.50m, 0m, 500m).PlatformOnly();

    /// <summary>Price of a cache read as a ratio of the input price (0.1; to check).</summary>
    public static readonly SettingDefinition<decimal> CacheReadRatio = SettingDefinitions.Number("ai.price_cache_read_ratio", 0.1m, 0m, 10m).PlatformOnly();

    /// <summary>Price of a cache write as a ratio of the input price (1.25 for a 5-minute cache; to check).</summary>
    public static readonly SettingDefinition<decimal> CacheWriteRatio = SettingDefinitions.Number("ai.price_cache_write_ratio", 1.25m, 0m, 10m).PlatformOnly();

    /// <summary>
    /// Estimated Meta cost of one billable reply in USD (0). TODO(spec): the agency's Meta rate for Tunisia ("Rest of
    /// Africa" service messages beyond the 1 000 free ones per month).
    /// </summary>
    public static readonly SettingDefinition<decimal> MetaUsdPerMessage = SettingDefinitions.Number("ai.meta_usd_per_message", 0m, 0m, 1m).PlatformOnly();

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        Enabled,
        MaxBotTurns,
        MaxTokens,
        MaxCostUsd,
        MaxModelCallsPerTurn,
        MaxToolCallsPerTurn,
        MaxStatusLookups,
        MaxTurnsWithoutProgress,
        InputUsdPerMillion,
        OutputUsdPerMillion,
        CacheReadRatio,
        CacheWriteRatio,
        MetaUsdPerMessage,
    ];
}

/// <summary>The settings a turn of the agent works with, read once per turn.</summary>
internal sealed record AgentTurnSettings(AgentCaps Caps, ModelPriceGrid Prices, decimal MetaUsdPerMessage)
{
    public static async Task<AgentTurnSettings> ReadAsync(ISettingsReader settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var caps = new AgentCaps(
            await settings.GetAsync(AgentSettings.MaxBotTurns, cancellationToken: cancellationToken),
            await settings.GetAsync(AgentSettings.MaxTokens, cancellationToken: cancellationToken),
            await settings.GetAsync(AgentSettings.MaxCostUsd, cancellationToken: cancellationToken),
            await settings.GetAsync(AgentSettings.MaxModelCallsPerTurn, cancellationToken: cancellationToken),
            await settings.GetAsync(AgentSettings.MaxToolCallsPerTurn, cancellationToken: cancellationToken),
            await settings.GetAsync(AgentSettings.MaxStatusLookups, cancellationToken: cancellationToken),
            await settings.GetAsync(AgentSettings.MaxTurnsWithoutProgress, cancellationToken: cancellationToken));
        var prices = new ModelPriceGrid(
            await settings.GetAsync(AgentSettings.InputUsdPerMillion, cancellationToken: cancellationToken),
            await settings.GetAsync(AgentSettings.OutputUsdPerMillion, cancellationToken: cancellationToken),
            await settings.GetAsync(AgentSettings.CacheReadRatio, cancellationToken: cancellationToken),
            await settings.GetAsync(AgentSettings.CacheWriteRatio, cancellationToken: cancellationToken));
        return new AgentTurnSettings(caps, prices, await settings.GetAsync(AgentSettings.MetaUsdPerMessage, cancellationToken: cancellationToken));
    }
}
