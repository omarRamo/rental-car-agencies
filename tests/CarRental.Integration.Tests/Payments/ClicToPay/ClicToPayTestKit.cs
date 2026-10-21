using System.Net;
using System.Text;
using CarRental.Application.Payments.Online;
using CarRental.Domain.Payments;
using CarRental.Infrastructure.Payments.ClicToPay;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace CarRental.Integration.Tests.Payments.ClicToPay;

/// <summary>Builds a <see cref="ClicToPayClient"/> on a scripted HTTP handler, with test credentials that must never leak.</summary>
internal static class ClicToPayTestKit
{
    public const string UserName = "merchant-api-user";
    public const string Password = "S3cret-Passw0rd!"; // gitleaks:allow (test-only credential checked for leaks)

    public static ClicToPayOptions Options(Action<ClicToPayOptions>? configure = null)
    {
        var options = new ClicToPayOptions
        {
            Enabled = true,
            BaseUrl = new Uri("https://test.clictopay.com/payment/rest/"),
            UserName = UserName,
            Password = Password,
            ReturnUrl = "https://www.agence.tn/payments/clictopay/return",
            FailUrl = "https://www.agence.tn/payments/clictopay/fail",
            StatusRetryDelayMilliseconds = 0,
            ReversedStatusCodes = "3",
            RefundedStatusCodes = "4",
            DeclinedStatusCodes = "6",
        };
        configure?.Invoke(options);
        return options;
    }

    public static ClicToPayClient Client(HttpMessageHandler handler, ClicToPayOptions options, IGatewayCallLog log, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler) { BaseAddress = options.BaseUrl, Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        return new ClicToPayClient(http, Microsoft.Extensions.Options.Options.Create(options), log, TimeProvider.System, NullLogger<ClicToPayClient>.Instance);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>An HTTP handler answering from a script, recording every request (method, address, content type, form).</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();

    public List<RecordedRequest> Requests { get; } = [];

    public ScriptedHandler Answer(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _script.Enqueue((_, _) => Task.FromResult(ClicToPayTestKit.Json(json, status)));
        return this;
    }

    public ScriptedHandler Status(HttpStatusCode status)
    {
        _script.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        return this;
    }

    public ScriptedHandler Fail(Exception exception)
    {
        _script.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(exception));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Content?.Headers.ContentType?.MediaType, QueryHelpers.ParseQuery(body)));
        var next = _script.Count > 0 ? _script.Dequeue() : (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        return await next(request, cancellationToken);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Address, string? ContentType, Dictionary<string, StringValues> Form)
{
    public string? Field(string name) => Form.TryGetValue(name, out var value) ? value.ToString() : null;
}

/// <summary>The call log in memory.</summary>
internal sealed class MemoryCallLog : IGatewayCallLog
{
    private readonly List<GatewayCallLogEntry> _entries = [];

    public IReadOnlyList<GatewayCallLogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public Task RecordAsync(GatewayCallLogEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }

        return Task.CompletedTask;
    }
}
