using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CarRental.Application.Messaging.Agent;
using Microsoft.Extensions.AI;

namespace CarRental.Application.Tests.Messaging.Agent.Fakes;

/// <summary>
/// One scripted answer of <see cref="ScriptedModel"/>: a final text, a tool call (arguments as JSON), a refusal, a reply
/// cut by the output limit, or a failure. Texts and arguments may quote the tool results of the request with
/// <c>{{tool:path}}</c> (<c>{{rechercher_disponibilites:options[0].total_ttc}}</c>, <c>{{creer_lien_devis:lien}}</c>).
/// </summary>
internal sealed record ModelStep
{
    public string? Text { get; init; }

    public IReadOnlyList<(string Tool, string Arguments)> Calls { get; init; } = [];

    public ChatFinishReason FinishReason { get; init; } = ChatFinishReason.Stop;

    public Func<Exception>? Failure { get; init; }

    public Func<CancellationToken, Task>? Gate { get; init; }

    public int InputTokens { get; init; } = 1_200;

    public int OutputTokens { get; init; } = 80;

    public int CacheReadTokens { get; init; } = 2_600;

    public int CacheWriteTokens { get; init; }

    public static ModelStep Reply(string text) => new() { Text = text };

    public static ModelStep Call(string tool, string arguments) => new() { Calls = [(tool, arguments)], FinishReason = ChatFinishReason.ToolCalls };

    public static ModelStep Calls2(params (string Tool, string Arguments)[] calls) => new() { Calls = calls, FinishReason = ChatFinishReason.ToolCalls };

    public static ModelStep Refusal() => new() { FinishReason = ChatFinishReason.ContentFilter };

    public static ModelStep Truncated(string text) => new() { Text = text, FinishReason = ChatFinishReason.Length };

    public static ModelStep Fail(Func<Exception> failure) => new() { Failure = failure };
}

/// <summary>
/// The scripted fake model of the application tests and of the CI evaluation (implements the model port): deterministic,
/// it returns the scripted steps in order — the last one repeats — with the usage they state (input counted with the
/// cache, as the Claude adapter does), and records every request.
/// </summary>
internal sealed partial class ScriptedModel : IAgentModel, IChatClient
{
    private readonly Lock _lock = new();
    private readonly List<ModelStep> _steps = [];
    private int _next;

    public bool IsAvailable { get; set; } = true;

    public string ModelId => "claude-haiku-5-5";

    public int MaxOutputTokens => 2048;

    public IChatClient Client => this;

    public List<(List<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

    public int Calls => Requests.Count;

    public ScriptedModel Then(params ModelStep[] steps)
    {
        lock (_lock)
        {
            _steps.AddRange(steps);
        }

        return this;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _steps.Clear();
            Requests.Clear();
            _next = 0;
        }
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = messages.ToList();
        ModelStep step;
        lock (_lock)
        {
            Requests.Add((request, options));
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("The scripted model has no step.");
            }

            step = _steps[Math.Min(_next, _steps.Count - 1)];
            _next++;
        }

        if (step.Gate is { } gate)
        {
            await gate(cancellationToken);
        }

        if (step.Failure is { } failure)
        {
            throw failure();
        }

        var contents = new List<AIContent>();
        if (step.Text is { } text)
        {
            contents.Add(new TextContent(Resolve(text, request)));
        }

        var index = 0;
        foreach (var (tool, arguments) in step.Calls)
        {
            var resolved = Resolve(arguments, request);
            contents.Add(new FunctionCallContent(
                string.Create(CultureInfo.InvariantCulture, $"toolu_{Requests.Count}_{index++}"),
                tool,
                JsonSerializer.Deserialize<Dictionary<string, object?>>(resolved)));
        }

        var usage = new UsageDetails
        {
            InputTokenCount = step.InputTokens + step.CacheReadTokens + step.CacheWriteTokens,
            CachedInputTokenCount = step.CacheReadTokens,
            OutputTokenCount = step.OutputTokens,
            AdditionalCounts = step.CacheWriteTokens > 0 ? new AdditionalPropertiesDictionary<long> { [IAgentModel.CacheWriteCountKey] = step.CacheWriteTokens } : null,
        };
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)) { FinishReason = step.FinishReason, Usage = usage };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    /// <summary>The JSON result of the latest call of <paramref name="tool"/> in <paramref name="messages"/>, or <see langword="null"/>.</summary>
    public static JsonElement? LatestResult(IReadOnlyList<ChatMessage> messages, string tool)
    {
        var calls = messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Where(c => c.Name == tool).Select(c => c.CallId).ToHashSet();
        var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault(r => calls.Contains(r.CallId));
        return result?.Result is string json ? JsonDocument.Parse(json).RootElement.Clone() : null;
    }

    /// <summary>Replaces <c>{{tool:path}}</c> by the value at <c>path</c> in the latest result of <c>tool</c>.</summary>
    public static string Resolve(string template, IReadOnlyList<ChatMessage> messages) =>
        Placeholder().Replace(template, match =>
        {
            var tool = match.Groups["tool"].Value;
            var element = LatestResult(messages, tool) ?? throw new InvalidOperationException($"No result of {tool} for {match.Value}.");
            foreach (Match segment in PathSegment().Matches(match.Groups["path"].Value))
            {
                element = segment.Groups["index"].Success
                    ? element[int.Parse(segment.Groups["index"].Value, CultureInfo.InvariantCulture)]
                    : element.GetProperty(segment.Groups["name"].Value);
            }

            return element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText();
        });

    [GeneratedRegex(@"\{\{(?<tool>[a-z_]+):(?<path>[^}]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"(?<name>[A-Za-z_][A-Za-z0-9_]*)|\[(?<index>\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex PathSegment();
}
