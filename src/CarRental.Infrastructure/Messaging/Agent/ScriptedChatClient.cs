using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CarRental.Application.Messaging.Agent;
using CarRental.Domain.Messaging;
using Microsoft.Extensions.AI;

namespace CarRental.Infrastructure.Messaging.Agent;

/// <summary>
/// A scripted answer of <see cref="ScriptedChatClient"/>: a text, a tool call, a refusal, a cut reply or a failure. Texts and
/// arguments may quote the latest tool results of the request: <c>{{rechercher_disponibilites:options[0].total_ttc}}</c>,
/// <c>{{creer_lien_devis:lien}}</c>.
/// </summary>
public sealed record ScriptedResponse
{
    private ScriptedResponse()
    {
    }

    public string? Text { get; private init; }

    public string? ToolName { get; private init; }

    public string? ToolArguments { get; private init; }

    public ChatFinishReason FinishReason { get; private init; } = ChatFinishReason.Stop;

    public Func<Exception>? Failure { get; private init; }

    /// <summary>Awaited before the answer is returned (a test holds the model call in the middle of a turn).</summary>
    public Func<CancellationToken, Task>? Gate { get; private init; }

    public int InputTokens { get; private init; } = 900;

    public int OutputTokens { get; private init; } = 60;

    public int CacheReadTokens { get; private init; } = 2_400;

    public int CacheWriteTokens { get; private init; }

    public static ScriptedResponse Reply(string text) => new() { Text = text };

    /// <summary>A call of <paramref name="tool"/> with arguments as a JSON object.</summary>
    public static ScriptedResponse Call(string tool, string argumentsJson) =>
        new() { ToolName = tool, ToolArguments = argumentsJson, FinishReason = ChatFinishReason.ToolCalls };

    public static ScriptedResponse Refusal() => new() { FinishReason = ChatFinishReason.ContentFilter };

    public static ScriptedResponse Truncated(string text) => new() { Text = text, FinishReason = ChatFinishReason.Length };

    public static ScriptedResponse Fail(Func<Exception> failure) => new() { Failure = failure };

    public ScriptedResponse WaitingFor(Func<CancellationToken, Task> gate) => this with { Gate = gate };

    public ScriptedResponse WithUsage(int input, int output, int cacheRead = 0, int cacheWrite = 0) =>
        this with { InputTokens = input, OutputTokens = output, CacheReadTokens = cacheRead, CacheWriteTokens = cacheWrite };
}

/// <summary>A request the scripted model received: the messages and the options, as sent.</summary>
public sealed record ScriptedRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);

/// <summary>
/// The model of Development and Testing (<c>Ai:Mode=Fake</c>, refused in Production): answers each conversation from the
/// script registered for its WhatsApp number (tests: integration, E2E), in order — the last answer repeats when the
/// script runs out —, with the usage the script states (input tokens counted with the cache, as the Claude adapter
/// does). A conversation without a script gets the demonstration reply. It records the requests and the concurrency of
/// the calls per number, so that tests check the requests and the serialization of the turns.
/// </summary>
public sealed partial class ScriptedChatClient : IChatClient
{
    public const string DemoReply =
        "Bonjour ! Je suis l'assistant de démonstration de l'agence : aucun modèle d'IA n'est configuré ici. Un conseiller vous répondra.";

    private readonly ConcurrentDictionary<string, ScriptState> _scripts = new(StringComparer.Ordinal);

    /// <summary>Registers the answers of the conversation of <paramref name="whatsAppNumber"/> (E.164 or <c>wa_id</c>).</summary>
    public void Script(string whatsAppNumber, params ScriptedResponse[] responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        if (responses.Length == 0)
        {
            throw new ArgumentException("A script has one answer at least.", nameof(responses));
        }

        _scripts[Key(whatsAppNumber)] = new ScriptState(responses);
    }

    public bool HasScript(string whatsAppNumber) => _scripts.ContainsKey(Key(whatsAppNumber));

    /// <summary>The requests received for the conversation of <paramref name="whatsAppNumber"/>, in order.</summary>
    public IReadOnlyList<ScriptedRequest> RequestsFor(string whatsAppNumber) =>
        _scripts.TryGetValue(Key(whatsAppNumber), out var script) ? script.Requests : [];

    /// <summary>Most calls of the conversation that were in progress at the same time (1 when its turns are serialized).</summary>
    public int MaxConcurrency(string whatsAppNumber) => _scripts.TryGetValue(Key(whatsAppNumber), out var script) ? script.MaxConcurrency : 0;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var key = options?.AdditionalProperties?.TryGetValue(IAgentModel.ScriptKeyProperty, out var value) == true && value is string number ? Key(number) : null;
        if (key is null || !_scripts.TryGetValue(key, out var script))
        {
            return Respond(ScriptedResponse.Reply(DemoReply), callIndex: 0, []);
        }

        var request = new ScriptedRequest([.. messages], options?.Clone());
        var (response, index) = script.Next(request);
        try
        {
            if (response.Gate is { } gate)
            {
                await gate(cancellationToken);
            }

            if (response.Failure is { } failure)
            {
                throw failure();
            }

            return Respond(response, index, request.Messages);
        }
        finally
        {
            script.Done();
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The WhatsApp agent does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType?.IsInstanceOfType(this) == true ? this : null;

    public void Dispose()
    {
        // Nothing to release.
    }

    private static ChatResponse Respond(ScriptedResponse response, int callIndex, IReadOnlyList<ChatMessage> messages)
    {
        var contents = new List<AIContent>();
        if (response.Text is { } text)
        {
            contents.Add(new Microsoft.Extensions.AI.TextContent(Resolve(text, messages)));
        }

        if (response.ToolName is { } tool)
        {
            var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(Resolve(response.ToolArguments ?? "{}", messages));
            contents.Add(new FunctionCallContent($"toolu_scripted_{callIndex}_{tool}", tool, arguments));
        }

        var usage = new UsageDetails
        {
            InputTokenCount = response.InputTokens + response.CacheReadTokens + response.CacheWriteTokens,
            CachedInputTokenCount = response.CacheReadTokens,
            OutputTokenCount = response.OutputTokens,
        };
        if (response.CacheWriteTokens > 0)
        {
            usage.AdditionalCounts = new AdditionalPropertiesDictionary<long> { [IAgentModel.CacheWriteCountKey] = response.CacheWriteTokens };
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)) { FinishReason = response.FinishReason, Usage = usage };
    }

    private static string Key(string number) =>
        WhatsAppNumbers.Normalize(number) is { IsSuccess: true } normalized ? normalized.Value : number.Trim();

    /// <summary>Replaces <c>{{tool:path}}</c> by the value at <c>path</c> in the latest result of <c>tool</c> among <paramref name="messages"/>.</summary>
    private static string Resolve(string template, IReadOnlyList<ChatMessage> messages) =>
        Placeholder().Replace(template, match =>
        {
            var tool = match.Groups["tool"].Value;
            var element = LatestResult(messages, tool) ?? throw new InvalidOperationException($"The script quotes {match.Value}, but {tool} returned nothing in this turn.");
            foreach (Match segment in PathSegment().Matches(match.Groups["path"].Value))
            {
                element = segment.Groups["index"].Success
                    ? element[int.Parse(segment.Groups["index"].Value, CultureInfo.InvariantCulture)]
                    : element.GetProperty(segment.Groups["name"].Value);
            }

            return element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText();
        });

    private static JsonElement? LatestResult(IReadOnlyList<ChatMessage> messages, string tool)
    {
        var calls = messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Where(c => c.Name == tool).Select(c => c.CallId).ToHashSet(StringComparer.Ordinal);
        var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault(r => calls.Contains(r.CallId));
        if (result?.Result is not string json)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [GeneratedRegex(@"\{\{(?<tool>[a-z_]+):(?<path>[^}]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"(?<name>[A-Za-z_][A-Za-z0-9_]*)|\[(?<index>\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex PathSegment();

    private sealed class ScriptState(IReadOnlyList<ScriptedResponse> responses)
    {
        private readonly Lock _lock = new();
        private readonly List<ScriptedRequest> _requests = [];
        private int _next;
        private int _inFlight;

        public int MaxConcurrency { get; private set; }

        public IReadOnlyList<ScriptedRequest> Requests
        {
            get
            {
                lock (_lock)
                {
                    return [.. _requests];
                }
            }
        }

        public (ScriptedResponse Response, int Index) Next(ScriptedRequest request)
        {
            lock (_lock)
            {
                _requests.Add(request);
                _inFlight++;
                MaxConcurrency = Math.Max(MaxConcurrency, _inFlight);
                var index = _next;
                _next++;
                return (responses[Math.Min(index, responses.Count - 1)], index);
            }
        }

        public void Done()
        {
            lock (_lock)
            {
                _inFlight--;
            }
        }
    }
}
