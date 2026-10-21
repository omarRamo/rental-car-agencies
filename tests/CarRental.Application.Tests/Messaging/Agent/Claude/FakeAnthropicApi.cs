using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CarRental.Application.Tests.Messaging.Agent.Claude;

/// <summary>
/// The Messages API of Anthropic, offline: the HTTP handler of the SDK client records every request body (the JSON the
/// SDK serialized) and answers with the queued responses — a message (text or tool use, stop reason, usage with the
/// cache counts) or an error status with the error body of the API.
/// </summary>
internal sealed class FakeAnthropicApi : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

    public List<JsonObject> Requests { get; } = [];

    public FakeAnthropicApi Then(HttpStatusCode status, string body)
    {
        _responses.Enqueue((status, body));
        return this;
    }

    public FakeAnthropicApi ThenText(string text, string stopReason = "end_turn", int input = 900, int output = 60, int cacheRead = 2_400, int cacheWrite = 0) =>
        Then(HttpStatusCode.OK, Message([new JsonObject { ["type"] = "text", ["text"] = text }], stopReason, input, output, cacheRead, cacheWrite));

    public FakeAnthropicApi ThenToolUse(string id, string name, JsonObject input) =>
        Then(HttpStatusCode.OK, Message([new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input }], "tool_use", 900, 40, 2_400, 0));

    public FakeAnthropicApi ThenError(HttpStatusCode status, string type, string message) =>
        Then(status, new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = message } }.ToJsonString());

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add(JsonNode.Parse(body)!.AsObject());
        }

        var (status, json) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, Message([new JsonObject { ["type"] = "text", ["text"] = "ok" }], "end_turn", 10, 1, 0, 0));
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.TryAddWithoutValidation("request-id", "req_test_" + Requests.Count.ToString(CultureInfo.InvariantCulture));
        return response;
    }

    private static string Message(JsonArray content, string stopReason, int input, int output, int cacheRead, int cacheWrite) =>
        new JsonObject
        {
            ["id"] = "msg_test",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = "claude-haiku-5-5",
            ["content"] = content,
            ["stop_reason"] = stopReason,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = input,
                ["output_tokens"] = output,
                ["cache_read_input_tokens"] = cacheRead,
                ["cache_creation_input_tokens"] = cacheWrite,
            },
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
