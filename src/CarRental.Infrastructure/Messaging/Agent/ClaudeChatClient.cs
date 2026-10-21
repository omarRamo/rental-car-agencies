using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using CarRental.Application.Messaging.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CarRental.Infrastructure.Messaging.Agent;

/// <summary>
/// Wraps the <see cref="IChatClient"/> of the Anthropic SDK (<c>AsIChatClient</c>, Claude API or Foundry) with what the
/// provider-agnostic agent leaves to the infrastructure (ADR-0055):
/// <list type="bullet">
/// <item><b>Prompt cache</b>: an explicit breakpoint (<c>cache_control</c>) on the last block of the leading system prompt —
/// the stable prefix, tools included since they render first — and the top-level automatic breakpoint for the growing
/// tail of the turn's tool loop (raw parameters of the SDK), with the configured TTL. A prefix below the model's
/// minimum is simply not cached: logged once.</item>
/// <item><b>Variable context</b>: a mid-conversation system message where the model and platform accept it; otherwise (Foundry,
/// or after a refusal of the API) merged as a delimited block into the last user turn — sticky for the process.</item>
/// <item><b>Errors</b>: a refusal for good (4xx but 429) becomes <see cref="AgentModelRejectedException"/>; a rate limit, an
/// overload, a server error, a connection failure or a timeout goes up as is, and the bot turn executor retries.</item>
/// <item>The <c>carrental.</c> properties of the options (scripted model key) never leave the process.</item>
/// </list>
/// </summary>
internal sealed partial class ClaudeChatClient : DelegatingChatClient
{
    private readonly string _model;
    private readonly AiOptions _settings;
    private readonly ILogger<ClaudeChatClient> _logger;
    private int _contextInUserTurn;
    private int _uncachedLogged;

    public ClaudeChatClient(IChatClient inner, string model, AiOptions settings, ILogger<ClaudeChatClient> logger)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _model = model;
        _settings = settings;
        _logger = logger;
        _contextInUserTurn = settings.ContextPlacement == AiContextPlacement.UserTurn
                             || (settings.ContextPlacement == AiContextPlacement.Auto && settings.Mode == AiMode.Foundry) ? 1 : 0;
    }

    private bool ContextInUserTurn => Volatile.Read(ref _contextInUserTurn) == 1;

    private CacheControlEphemeral CacheControl => new() { Ttl = _settings.PromptCacheTtl == "1h" ? Ttl.Ttl1h : Ttl.Ttl5m };

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var history = messages.ToList();
        var callOptions = Prepare(options);
        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(Prepare(history, ContextInUserTurn), callOptions, cancellationToken);
        }
        catch (AnthropicBadRequestException exception) when (!ContextInUserTurn && MentionsSystemRole(exception))
        {
            // The model or the platform refuses mid-conversation system messages: the context goes into the user turn.
            Interlocked.Exchange(ref _contextInUserTurn, 1);
            LogContextMovedToUserTurn(_model);
            response = await RetryAsync(history, callOptions, cancellationToken);
        }
        catch (Exception exception) when (IsRejection(exception))
        {
            throw new AgentModelRejectedException($"The model service refused the request ({exception.GetType().Name}).", exception);
        }

        CheckCache(response.Usage);
        return response;
    }

    /// <summary>The leading system prompt with its cache breakpoint; the variable context in place or in the last user turn.</summary>
    internal List<ChatMessage> Prepare(IReadOnlyList<ChatMessage> messages, bool contextInUserTurn)
    {
        var prepared = new List<ChatMessage>(messages.Count);
        var leading = true;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role == ChatRole.System && leading)
            {
                var nextIsSystem = i + 1 < messages.Count && messages[i + 1].Role == ChatRole.System;
                prepared.Add(nextIsSystem ? message : WithBreakpoint(message));
                continue;
            }

            leading = false;
            var userIndex = prepared.FindLastIndex(m => m.Role == ChatRole.User);
            if (message.Role == ChatRole.System && contextInUserTurn && userIndex >= 0)
            {
                var user = prepared[userIndex];
                prepared[userIndex] = new ChatMessage(ChatRole.User, [.. user.Contents, new TextContent(message.Text)]);
                continue;
            }

            prepared.Add(message);
        }

        return prepared;
    }

    private async Task<ChatResponse> RetryAsync(List<ChatMessage> history, ChatOptions callOptions, CancellationToken cancellationToken)
    {
        try
        {
            return await base.GetResponseAsync(Prepare(history, contextInUserTurn: true), callOptions, cancellationToken);
        }
        catch (Exception exception) when (IsRejection(exception))
        {
            throw new AgentModelRejectedException($"The model service refused the request ({exception.GetType().Name}).", exception);
        }
    }

    private ChatOptions Prepare(ChatOptions? source)
    {
        var options = source?.Clone() ?? new ChatOptions();
        if (options.AdditionalProperties is { } properties)
        {
            foreach (var key in properties.Keys.Where(k => k.StartsWith("carrental.", StringComparison.Ordinal)).ToList())
            {
                properties.Remove(key);
            }
        }

        var maxTokens = options.MaxOutputTokens ?? _settings.MaxOutputTokens;
        var modelId = options.ModelId ?? _model;
        var cacheControl = CacheControl;

        // The raw parameters carry what the IChatClient options cannot: the top-level (automatic) cache breakpoint.
        options.RawRepresentationFactory = _ => new MessageCreateParams
        {
            MaxTokens = maxTokens,
            Model = modelId,
            Messages = [],
            CacheControl = cacheControl,
        };
        return options;
    }

    private ChatMessage WithBreakpoint(ChatMessage system)
    {
        var contents = system.Contents.ToList();
        var last = contents.FindLastIndex(c => c is TextContent);
        if (last < 0)
        {
            return system;
        }

        contents[last] = new TextContent(((TextContent)contents[last]).Text).WithCacheControl(CacheControl);
        return new ChatMessage(ChatRole.System, contents);
    }

    private void CheckCache(UsageDetails? usage)
    {
        var written = usage?.AdditionalCounts?.TryGetValue(IAgentModel.CacheWriteCountKey, out var count) == true ? count : 0;
        if (usage is not null && (usage.CachedInputTokenCount ?? 0) == 0 && written == 0 && Interlocked.Exchange(ref _uncachedLogged, 1) == 0)
        {
            LogNotCached(_model, usage.InputTokenCount ?? 0);
        }
    }

    private static bool MentionsSystemRole(AnthropicBadRequestException exception) =>
        exception.Message.Contains("system", StringComparison.OrdinalIgnoreCase)
        && exception.Message.Contains("role", StringComparison.OrdinalIgnoreCase);

    /// <summary>A refusal for good: a 4xx answer other than a rate limit (retrying would get the same answer).</summary>
    private static bool IsRejection(Exception exception) => exception is Anthropic4xxException and not AnthropicRateLimitException;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Claude model {Model} refuses mid-conversation system messages: the turn context now goes into the user turn.")]
    private partial void LogContextMovedToUserTurn(string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "Claude model {Model}: the prompt ({InputTokens} input tokens) was not cached (below the minimum cacheable prefix of the model?). Logged once.")]
    private partial void LogNotCached(string model, long inputTokens);
}
