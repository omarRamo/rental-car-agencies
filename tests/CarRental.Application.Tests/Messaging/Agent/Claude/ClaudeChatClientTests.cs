using System.Net;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using CarRental.Application.Messaging.Agent;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Tests.Messaging.Agent.Fakes;
using CarRental.Infrastructure.Messaging.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CarRental.Application.Tests.Messaging.Agent.Claude;

/// <summary>
/// The Claude adapter of the agent (ADR-0055) over the real Anthropic SDK, offline: the JSON the SDK sends to the Messages
/// API — the cache breakpoint after the system prompt and the top-level one, the five tools, the turn context as a
/// mid-conversation system message after the transcript (or in the last user turn when refused), the low effort, no
/// platform property — and what comes back: the cache reads and writes, a refusal, a cut reply, the tool calls, and the
/// errors that are final (hand-over) or transient (retry by the executor).
/// </summary>
public sealed class ClaudeChatClientTests : IDisposable
{
    private const string Model = "claude-haiku-5-5";

    private readonly FakeAnthropicApi _api = new();
    private readonly AnthropicClient _client;

    public ClaudeChatClientTests()
    {
        _client = new AnthropicClient { ApiKey = "sk-test-not-a-key", HttpClient = new HttpClient(_api), MaxRetries = 0 };
    }

    public void Dispose() => _client.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_request_caches_the_prefix_and_puts_the_turn_context_after_the_transcript()
    {
        _api.ThenText("Bonjour !");
        using var claude = Claude();

        await claude.GetResponseAsync(Conversation(), Options(), Ct);

        var request = _api.Requests.ShouldHaveSingleItem();
        request["model"]!.GetValue<string>().ShouldBe(Model);
        request["max_tokens"]!.GetValue<int>().ShouldBe(2048);
        var system = request["system"]!.AsArray();
        system[^1]!["text"]!.GetValue<string>().ShouldBe("PROMPT");
        system[^1]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
        system[^1]!["cache_control"]!["ttl"]!.GetValue<string>().ShouldBe("5m");
        request["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral", "the automatic breakpoint caches the growing tail of the tool loop");
        request["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ShouldBe(["infos_agence", "statut_reservation"]);
        request["tool_choice"]!["type"]!.GetValue<string>().ShouldBe("auto");
        request["output_config"]!["effort"]!.GetValue<string>().ShouldBe("low");
        var messages = request["messages"]!.AsArray();
        messages.Select(m => m!["role"]!.GetValue<string>()).ShouldBe(["user", "system"]);
        messages[1]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("<contexte_plateforme>CTX</contexte_plateforme>");
        request.ToJsonString().ShouldNotContain("carrental", Case.Insensitive);
        request.ToJsonString().ShouldNotContain("+21698123456");
    }

    [Fact]
    public async Task A_one_hour_cache_is_asked_when_configured()
    {
        _api.ThenText("Bonjour !");
        using var claude = Claude(new AiOptions { Mode = AiMode.Anthropic, ApiKey = "sk-test", PromptCacheTtl = "1h" });

        await claude.GetResponseAsync(Conversation(), Options(), Ct);

        var request = _api.Requests.ShouldHaveSingleItem();
        request["system"]!.AsArray()[^1]!["cache_control"]!["ttl"]!.GetValue<string>().ShouldBe("1h");
        request["cache_control"]!["ttl"]!.GetValue<string>().ShouldBe("1h");
    }

    [Fact]
    public async Task The_usage_counts_the_cache_reads_and_writes_as_the_agent_prices_them()
    {
        _api.ThenText("Bonjour !", input: 900, output: 60, cacheRead: 2_400, cacheWrite: 300);
        using var claude = Claude();

        var response = await claude.GetResponseAsync(Conversation(), Options(), Ct);

        var usage = response.Usage.ShouldNotBeNull();
        usage.InputTokenCount.ShouldBe(3_600, "the input count of the adapter includes the cache reads and writes");
        usage.CachedInputTokenCount.ShouldBe(2_400);
        usage.OutputTokenCount.ShouldBe(60);
        usage.AdditionalCounts.ShouldNotBeNull()[IAgentModel.CacheWriteCountKey].ShouldBe(300);
        var run = new AgentRun();
        run.Add(usage);
        run.Tokens.ShouldBe(new Domain.Messaging.Agent.ModelTokenUsage(900, 60, 2_400, 300));
    }

    [Theory]
    [InlineData("refusal", "content_filter")]
    [InlineData("max_tokens", "length")]
    [InlineData("end_turn", "stop")]
    public async Task The_stop_reason_of_the_api_is_the_finish_reason_the_agent_reads(string stopReason, string finishReason)
    {
        _api.ThenText("…", stopReason: stopReason);
        using var claude = Claude();

        var response = await claude.GetResponseAsync(Conversation(), Options(), Ct);

        response.FinishReason.ShouldBe(new ChatFinishReason(finishReason));
    }

    [Fact]
    public async Task A_tool_call_comes_back_as_a_function_call_and_its_error_result_is_marked_as_an_error()
    {
        _api.ThenToolUse("toolu_1", "infos_agence", new JsonObject { ["sujet"] = "caution" }).ThenText("La caution est restituée au retour.");
        using var claude = Claude();
        var messages = Conversation();

        var first = await claude.GetResponseAsync(messages, Options(), Ct);
        var call = first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ShouldHaveSingleItem();
        call.Name.ShouldBe("infos_agence");
        call.Arguments.ShouldNotBeNull()["sujet"]!.ToString().ShouldBe("caution");
        first.FinishReason.ShouldBe(ChatFinishReason.ToolCalls);
        messages.AddRange(first.Messages);
        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, """{"erreur":"Arguments invalides."}""") { Exception = new InvalidOperationException("tool error") }]));
        await claude.GetResponseAsync(messages, Options(), Ct);

        var second = _api.Requests[1]["messages"]!.AsArray();
        var result = second.Select(m => m!["content"]!.AsArray()).SelectMany(c => c).Single(b => b!["type"]!.GetValue<string>() == "tool_result")!;
        result["tool_use_id"]!.GetValue<string>().ShouldBe("toolu_1");
        result["is_error"]!.GetValue<bool>().ShouldBeTrue("an error of a tool is flagged to the model");
        second.Select(m => m!["content"]!.AsArray()).SelectMany(c => c).ShouldContain(b => b!["type"]!.GetValue<string>() == "tool_use");
    }

    [Fact]
    public async Task When_the_api_refuses_system_messages_the_context_moves_into_the_last_user_turn_for_good()
    {
        _api.ThenError(HttpStatusCode.BadRequest, "invalid_request_error", "messages.1: the system role is not supported in messages for this model")
            .ThenText("Bonjour !")
            .ThenText("Encore bonjour !");
        using var claude = Claude();

        (await claude.GetResponseAsync(Conversation(), Options(), Ct)).Text.ShouldBe("Bonjour !");
        await claude.GetResponseAsync(Conversation(), Options(), Ct);

        _api.Requests.Count.ShouldBe(3);
        foreach (var request in _api.Requests.Skip(1))
        {
            var messages = request["messages"]!.AsArray();
            messages.Select(m => m!["role"]!.GetValue<string>()).ShouldBe(["user"]);
            messages[0]!["content"]!.AsArray().Select(b => b!["text"]!.GetValue<string>()).ShouldBe(["Bonjour", "<contexte_plateforme>CTX</contexte_plateforme>"]);
        }
    }

    [Fact]
    public async Task On_foundry_the_context_goes_into_the_user_turn_from_the_start()
    {
        _api.ThenText("Bonjour !");
        using var claude = Claude(new AiOptions { Mode = AiMode.Foundry, Foundry = { Resource = "agence-ia" } });

        await claude.GetResponseAsync(Conversation(), Options(), Ct);

        _api.Requests.ShouldHaveSingleItem()["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()).ShouldBe(["user"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_error")]
    [InlineData(HttpStatusCode.Forbidden, "permission_error")]
    [InlineData(HttpStatusCode.BadRequest, "invalid_request_error")]
    [InlineData(HttpStatusCode.NotFound, "not_found_error")]
    public async Task A_request_refused_for_good_is_a_rejection(HttpStatusCode status, string type)
    {
        _api.ThenError(status, type, "Refused.");
        using var claude = Claude();

        await Should.ThrowAsync<AgentModelRejectedException>(() => claude.GetResponseAsync(Conversation(), Options(), Ct));
    }

    [Theory]
    [InlineData((HttpStatusCode)429, "rate_limit_error")]
    [InlineData((HttpStatusCode)529, "overloaded_error")]
    [InlineData(HttpStatusCode.InternalServerError, "api_error")]
    public async Task A_transient_failure_goes_up_for_a_retry_of_the_turn(HttpStatusCode status, string type)
    {
        _api.ThenError(status, type, "Try again later.");
        using var claude = Claude();

        var exception = await Should.ThrowAsync<Exception>(() => claude.GetResponseAsync(Conversation(), Options(), Ct));

        exception.ShouldNotBeOfType<AgentModelRejectedException>("the executor retries the turn, then hands it over");
        exception.ShouldBeAssignableTo<AnthropicException>();
    }

    [Fact]
    public async Task The_agent_sends_its_real_prompt_tools_transcript_and_context_through_the_adapter()
    {
        _api.ThenText("Bonjour ! Pour quelles dates et quel lieu de prise en charge ?");
        using var claude = Claude();
        await using var harness = new AgentHarness(agentModel: new ConfiguredAgentModel(claude, Model, 2048, owned: null));

        var reply = await harness.NewConversation().SendAsync("Bonjour, une voiture pour juillet ?");

        reply.Message.ShouldBeOfType<OutboundText>().Body.ShouldBe("Bonjour ! Pour quelles dates et quel lieu de prise en charge ?");
        var usage = reply.Usage.ShouldNotBeNull();
        usage.CacheReadTokens.ShouldBe(2_400);
        usage.InputTokens.ShouldBe(900);
        var request = _api.Requests.ShouldHaveSingleItem();
        var system = request["system"]!.AsArray();
        system[^1]!["text"]!.GetValue<string>().ShouldStartWith("Tu es l'assistant WhatsApp de Agence Démo Tunis");
        system[^1]!["cache_control"].ShouldNotBeNull();
        request["tools"]!.AsArray().Count.ShouldBe(5);
        var messages = request["messages"]!.AsArray();
        messages[0]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("Bonjour, une voiture pour juillet ?");
        messages[^1]!["role"]!.GetValue<string>().ShouldBe("system");
        messages[^1]!["content"]![0]!["text"]!.GetValue<string>().ShouldContain("Nous sommes le dimanche 20 juin 2027 à 09:00 (heure de Tunis).");
        request.ToJsonString().ShouldNotContain("21698123456");
    }

    private ClaudeChatClient Claude(AiOptions? settings = null) =>
        new(_client.AsIChatClient(Model, 2048), Model, settings ?? new AiOptions { Mode = AiMode.Anthropic, ApiKey = "sk-test" }, NullLogger<ClaudeChatClient>.Instance);

    private static List<ChatMessage> Conversation() =>
        [new(ChatRole.System, "PROMPT"), new(ChatRole.User, "Bonjour"), new(ChatRole.System, "<contexte_plateforme>CTX</contexte_plateforme>")];

    private static ChatOptions Options() => new()
    {
        ModelId = Model,
        MaxOutputTokens = 2048,
        Tools =
        [
            AIFunctionFactory.CreateDeclaration("infos_agence", "Infos.", System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"sujet":{"type":"string"}},"required":["sujet"]}""").RootElement.Clone()),
            AIFunctionFactory.CreateDeclaration("statut_reservation", "Statut.", System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"reference":{"type":"string"}},"required":["reference"]}""").RootElement.Clone()),
        ],
        ToolMode = ChatToolMode.Auto,
        AllowMultipleToolCalls = true,
        Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
        AdditionalProperties = new AdditionalPropertiesDictionary { [IAgentModel.ScriptKeyProperty] = "+21698123456" },
    };
}
