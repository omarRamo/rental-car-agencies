using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CarRental.Application.Messaging.Sending;
using CarRental.Domain.Messaging;
using CarRental.Infrastructure.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CarRental.Integration.Tests.Messaging;

/// <summary>
/// The Graph API client over real HTTP against a local stub of <c>POST /{version}/{phone-number-id}/messages</c>: the
/// request shape (path, bearer token, JSON of each content as documented by Meta) and the classification of the answers
/// (accepted, refused, transient, unknown).
/// </summary>
public sealed class WhatsAppGraphClientTests : IAsyncLifetime
{
    private const string Token = "test-access-token"; // gitleaks:allow (test-only value sent to a local stub)
    private const string PhoneNumberId = "106540352242922";

    private readonly ConcurrentQueue<(string Path, string? Authorization, string ContentType, JsonDocument Body)> _requests = new();
    private Func<HttpContext, Task> _respond = context => Accepted(context, "wamid.HBgLMjE2OTgxMjM0NTYVAgARGBI1");
    private WebApplication _stub = null!;
    private Uri _address = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _stub = builder.Build();
        _stub.MapPost("/{version}/{phoneNumberId}/messages", async (HttpContext context) =>
        {
            var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            _requests.Enqueue((context.Request.Path.Value!, context.Request.Headers.Authorization.ToString(), context.Request.ContentType ?? string.Empty, body));
            await _respond(context);
        });
        await _stub.StartAsync(Ct);
        _address = new Uri(_stub.Urls.First() + "/");
    }

    public async ValueTask DisposeAsync()
    {
        await _stub.DisposeAsync();
        foreach (var (_, _, _, body) in _requests)
        {
            body.Dispose();
        }
    }

    [Fact]
    public async Task A_text_is_posted_with_the_bearer_token_to_the_messages_of_the_number()
    {
        var result = await Client().SendAsync(new WhatsAppSendRequest("21698123456", TextContent.Create("Bonjour !").Value), Ct);

        result.ShouldBe(WhatsAppSendResult.Accepted("wamid.HBgLMjE2OTgxMjM0NTYVAgARGBI1"));
        var (path, authorization, contentType, body) = _requests.ShouldHaveSingleItem();
        path.ShouldBe($"/v23.0/{PhoneNumberId}/messages");
        authorization.ShouldBe($"Bearer {Token}");
        contentType.ShouldStartWith("application/json");
        var root = body.RootElement;
        root.GetProperty("messaging_product").GetString().ShouldBe("whatsapp");
        root.GetProperty("recipient_type").GetString().ShouldBe("individual");
        root.GetProperty("to").GetString().ShouldBe("21698123456");
        root.GetProperty("type").GetString().ShouldBe("text");
        root.GetProperty("text").GetProperty("body").GetString().ShouldBe("Bonjour !");
        root.GetProperty("text").GetProperty("preview_url").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Reply_buttons_a_list_and_a_template_follow_the_documented_shapes()
    {
        var buttons = ButtonsContent.Create("Quelle catégorie ?", [ReplyButton.Create("cat:ECMR", "Économique").Value, ReplyButton.Create("cat:IFMR", "SUV").Value], footer: "Prix TTC").Value;
        var list = ListContent.Create(
            "Où prenez-vous la voiture ?",
            "Choisir",
            [ListSection.Create("Aéroports", [ListRow.Create("TUN", "Tunis-Carthage", "Terminal 1").Value]).Value, ListSection.Create("Agences", [ListRow.Create("AGC", "Tunis centre").Value]).Value]).Value;
        var template = TemplateContent.Create("booking_confirmed", "fr", ["Amel", "LOC-2027-000123"]).Value;
        var client = Client();

        await client.SendAsync(new WhatsAppSendRequest("21698123456", buttons), Ct);
        await client.SendAsync(new WhatsAppSendRequest("21698123456", list), Ct);
        await client.SendAsync(new WhatsAppSendRequest("21698123456", template), Ct);

        var bodies = _requests.Select(r => r.Body.RootElement).ToList();
        var interactive = bodies[0].GetProperty("interactive");
        bodies[0].GetProperty("type").GetString().ShouldBe("interactive");
        interactive.GetProperty("type").GetString().ShouldBe("button");
        interactive.GetProperty("body").GetProperty("text").GetString().ShouldBe("Quelle catégorie ?");
        interactive.GetProperty("footer").GetProperty("text").GetString().ShouldBe("Prix TTC");
        interactive.TryGetProperty("header", out _).ShouldBeFalse();
        var reply = interactive.GetProperty("action").GetProperty("buttons")[0];
        reply.GetProperty("type").GetString().ShouldBe("reply");
        reply.GetProperty("reply").GetProperty("id").GetString().ShouldBe("cat:ECMR");
        reply.GetProperty("reply").GetProperty("title").GetString().ShouldBe("Économique");

        var listAction = bodies[1].GetProperty("interactive").GetProperty("action");
        bodies[1].GetProperty("interactive").GetProperty("type").GetString().ShouldBe("list");
        listAction.GetProperty("button").GetString().ShouldBe("Choisir");
        listAction.GetProperty("sections")[0].GetProperty("title").GetString().ShouldBe("Aéroports");
        listAction.GetProperty("sections")[0].GetProperty("rows")[0].GetProperty("description").GetString().ShouldBe("Terminal 1");
        listAction.GetProperty("sections")[1].GetProperty("rows")[0].TryGetProperty("description", out _).ShouldBeFalse();

        var templateBody = bodies[2].GetProperty("template");
        bodies[2].GetProperty("type").GetString().ShouldBe("template");
        templateBody.GetProperty("name").GetString().ShouldBe("booking_confirmed");
        templateBody.GetProperty("language").GetProperty("code").GetString().ShouldBe("fr");
        var component = templateBody.GetProperty("components")[0];
        component.GetProperty("type").GetString().ShouldBe("body");
        component.GetProperty("parameters").EnumerateArray().Select(p => (p.GetProperty("type").GetString(), p.GetProperty("text").GetString()))
            .ShouldBe([("text", "Amel"), ("text", "LOC-2027-000123")]);
    }

    [Theory]
    [InlineData(400, 131047, WhatsAppSendOutcome.Rejected)]
    [InlineData(400, 132001, WhatsAppSendOutcome.Rejected)]
    [InlineData(400, 100, WhatsAppSendOutcome.Rejected)]
    [InlineData(401, 190, WhatsAppSendOutcome.Transient)]
    [InlineData(403, 10, WhatsAppSendOutcome.Transient)]
    [InlineData(403, 200, WhatsAppSendOutcome.Transient)]
    [InlineData(429, 130429, WhatsAppSendOutcome.Transient)]
    [InlineData(400, 131056, WhatsAppSendOutcome.Transient)]
    [InlineData(503, 131016, WhatsAppSendOutcome.Transient)]
    [InlineData(500, 131000, WhatsAppSendOutcome.Transient)]
    public async Task An_error_of_the_graph_api_is_classified_by_its_code(int status, int code, WhatsAppSendOutcome expected)
    {
        _respond = context => Error(context, status, code, $"(#{code}) Error of the stub");

        var result = await Client().SendAsync(new WhatsAppSendRequest("21698123456", TextContent.Create("Bonjour").Value), Ct);

        result.Outcome.ShouldBe(expected);
        result.ErrorCode.ShouldBe(code);
        result.ErrorTitle.ShouldBe($"(#{code}) Error of the stub");
        result.MetaMessageId.ShouldBeNull();
    }

    [Fact]
    public async Task A_server_failure_without_an_error_of_the_graph_api_is_an_unknown_outcome()
    {
        _respond = context =>
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return Task.CompletedTask;
        };

        (await Client().SendAsync(new WhatsAppSendRequest("21698123456", TextContent.Create("Bonjour").Value), Ct)).Outcome.ShouldBe(WhatsAppSendOutcome.Unknown);
    }

    [Fact]
    public async Task A_timeout_after_the_request_left_is_an_unknown_outcome()
    {
        var never = new TaskCompletionSource();
        _respond = _ => never.Task;
        try
        {
            var result = await Client(timeout: TimeSpan.FromSeconds(1)).SendAsync(new WhatsAppSendRequest("21698123456", TextContent.Create("Bonjour").Value), Ct);

            result.Outcome.ShouldBe(WhatsAppSendOutcome.Unknown);
        }
        finally
        {
            never.TrySetResult();
        }
    }

    [Fact]
    public async Task A_connection_that_cannot_be_opened_is_transient()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await Client(new Uri($"http://127.0.0.1:{port}/")).SendAsync(new WhatsAppSendRequest("21698123456", TextContent.Create("Bonjour").Value), Ct);

        result.Outcome.ShouldBe(WhatsAppSendOutcome.Transient);
    }

    [Fact]
    public async Task An_answer_without_a_message_id_is_an_unknown_outcome()
    {
        _respond = async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"messaging_product":"whatsapp","contacts":[]}""");
        };

        (await Client().SendAsync(new WhatsAppSendRequest("21698123456", TextContent.Create("Bonjour").Value), Ct)).Outcome.ShouldBe(WhatsAppSendOutcome.Unknown);
    }

    private static async Task Accepted(HttpContext context, string id)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            $$$"""{"messaging_product":"whatsapp","contacts":[{"input":"21698123456","wa_id":"21698123456"}],"messages":[{"id":"{{{id}}}"}]}""");
    }

    private static async Task Error(HttpContext context, int status, int code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            $$$"""{"error":{"message":"{{{message}}}","type":"OAuthException","code":{{{code}}},"error_data":{"messaging_product":"whatsapp","details":"stub"},"fbtrace_id":"AbCdEf"}}""");
    }

    private WhatsAppGraphClient Client(Uri? address = null, TimeSpan? timeout = null) => new(
        new HttpClient { BaseAddress = address ?? _address, Timeout = timeout ?? TimeSpan.FromSeconds(10) },
        Options.Create(new WhatsAppOptions
        {
            Mode = WhatsAppMode.Graph,
            ApiVersion = "v23.0",
            PhoneNumberId = PhoneNumberId,
            AccessToken = Token,
        }),
        NullLogger<WhatsAppGraphClient>.Instance);
}
