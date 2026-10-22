using Microsoft.Extensions.AI;

namespace CarRental.Application.Messaging.Agent;

/// <summary>
/// The model of the WhatsApp agent (ADR-0055): an <see cref="IChatClient"/> of Microsoft.Extensions.AI — the Claude
/// adapter of the official Anthropic SDK (Claude API or Microsoft Foundry), or the scripted fake of development and
/// tests — with the model id and the output limit of a call. <see cref="IsAvailable"/> is false when no model is
/// configured (<c>Ai:Mode=Disabled</c>): the bot then stays silent and agents answer.
/// </summary>
/// <remarks>
/// The client follows the conventions of the Claude adapter: <see cref="UsageDetails.InputTokenCount"/> counts every
/// input token (uncached, read from and written to the cache), <see cref="UsageDetails.CachedInputTokenCount"/> the cache
/// reads, <see cref="UsageDetails.AdditionalCounts"/>[<see cref="CacheWriteCountKey"/>] the cache writes; a refusal
/// ends with <see cref="ChatFinishReason.ContentFilter"/>, the output limit with <see cref="ChatFinishReason.Length"/>.
/// Transient failures (rate limit, overload, server error, timeout) are exceptions: the bot turn executor retries the
/// turn; a request the provider refuses for good surfaces as <see cref="AgentModelRejectedException"/>.
/// </remarks>
public interface IAgentModel
{
    bool IsAvailable { get; }

    /// <summary>The model id, e.g. <c>claude-haiku-5-5</c>.</summary>
    string ModelId { get; }

    /// <summary>Output limit of one call (thinking included).</summary>
    int MaxOutputTokens { get; }

    IChatClient Client { get; }

    /// <summary>Key of the cache writes in <see cref="UsageDetails.AdditionalCounts"/> (the Claude adapter's name).</summary>
    const string CacheWriteCountKey = "CacheCreationInputTokens";

    /// <summary>
    /// Key of <see cref="ChatOptions.AdditionalProperties"/> carrying the conversation's WhatsApp number for the scripted
    /// fake model (it picks the script of the number). Never sent to a provider: the Claude adapter ignores these
    /// properties and the infrastructure removes every <c>carrental.</c> key before a real call.
    /// </summary>
    const string ScriptKeyProperty = "carrental.script_key";
}

/// <summary>The provider refused the request for good (invalid request, authentication, permission): retrying would not help.</summary>
public sealed class AgentModelRejectedException : Exception
{
    public AgentModelRejectedException()
    {
    }

    public AgentModelRejectedException(string message)
        : base(message)
    {
    }

    public AgentModelRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The texts of the system prompt of the WhatsApp agent: the template of PROMPT.md annexe B, verbatim (variables
/// <c>{{AGENCY_NAME}}</c>, <c>{{AGENCY_CITY}}</c>, <c>{{TODAY}}</c>, <c>{{OPENING_HOURS}}</c>, <c>{{AGENCY_ADDRESS}}</c>),
/// and a technical addendum (tool results are data, numbering of the options, the context block). Embedded resources
/// of the infrastructure (<c>Messaging/Prompts</c>).
/// </summary>
public interface IAgentPromptSource
{
    string SystemPromptTemplate { get; }

    string Addendum { get; }
}

/// <summary>
/// Where the agent sends customers (section <c>PublicSite</c>): the absolute address of the public site (links of the
/// booking funnel). The agency it presents is the agency of each conversation (ADR-0067).
/// </summary>
public sealed class AgentSiteOptions
{
    public Uri? PublicBaseUrl { get; set; }
}

/// <summary>A tool failed for the model: its result is an error (malformed input, unknown option, failure of an engine).</summary>
internal sealed class AgentToolFailure : Exception
{
    public AgentToolFailure()
    {
    }

    public AgentToolFailure(string message)
        : base(message)
    {
    }

    public AgentToolFailure(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
