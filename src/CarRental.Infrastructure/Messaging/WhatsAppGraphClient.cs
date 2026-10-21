using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CarRental.Application.Messaging.Sending;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Messaging;

/// <summary>
/// <see cref="IWhatsAppClient"/> over the Graph API (typed <see cref="HttpClient"/>): <c>POST
/// {GraphBaseUrl}/{ApiVersion}/{PhoneNumberId}/messages</c> with the access token as a bearer token, the answer read as
/// documented by Meta (<c>messages[0].id</c>, or <c>error.code</c>, <c>error.message</c>). Every call is logged with
/// its kind, HTTP status, Meta error code and duration — never the token nor the body (personal data).
/// </summary>
/// <remarks>
/// Retries only where safe: the client itself never retries a send (a message is not idempotent at Meta). It classifies
/// the outcome for <see cref="OutboundMessageSender"/>: rate limits (HTTP 429, codes 4, 80007, 130429, 131056),
/// temporary unavailability (codes 2, 131000, 131016, HTTP 503) and a refused access token or permission (HTTP 401,
/// codes 0, 10, 190, 200–299: an expired token must not lose the messages for good) are transient — the request was
/// refused, retrying later is safe; a connection that could not be opened is transient too; a timeout or a broken
/// answer after the request was sent is unknown; any other error answer is a refusal.
/// </remarks>
internal sealed partial class WhatsAppGraphClient(HttpClient httpClient, IOptions<WhatsAppOptions> options, ILogger<WhatsAppGraphClient> logger)
    : IWhatsAppClient
{
    private static readonly HashSet<int> TransientCodes = [2, 4, 80007, 130429, 131000, 131016, 131056];

    public bool IsEnabled => true;

    public async Task<WhatsAppSendResult> SendAsync(WhatsAppSendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.Value;
        var kind = request.Content.Kind.ToString();
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"{settings.ApiVersion}/{settings.PhoneNumberId}/messages", UriKind.Relative))
        {
            Content = new ByteArrayContent(GraphMessagePayload.Create(request.To, request.Content)),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var started = Stopwatch.GetTimestamp();
        try
        {
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var result = Interpret(response.StatusCode, body);
            LogCall(kind, (int)response.StatusCode, result.ErrorCode, result.Outcome.ToString(), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (IsAccessRefused(response.StatusCode, result.ErrorCode))
            {
                LogAccessRefused(result.ErrorCode);
            }

            return result;
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError is HttpRequestError.ConnectionError
                                                                               or HttpRequestError.NameResolutionError
                                                                               or HttpRequestError.SecureConnectionError
                                                                               or HttpRequestError.ProxyTunnelError)
        {
            // The connection could not be opened: the message certainly did not leave.
            LogNotSent(exception, kind, exception.HttpRequestError.ToString());
            return WhatsAppSendResult.Transient();
        }
        catch (HttpRequestException exception)
        {
            LogNoAnswer(exception, kind);
            return WhatsAppSendResult.Unknown();
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogNoAnswer(exception, kind);
            return WhatsAppSendResult.Unknown();
        }
    }

    /// <summary>The outcome of an answer of the Graph API (see the remarks of the class).</summary>
    internal static WhatsAppSendResult Interpret(HttpStatusCode status, ReadOnlySpan<byte> body)
    {
        var (messageId, errorCode, errorTitle, readable) = Read(body);
        if ((int)status is >= 200 and < 300)
        {
            return string.IsNullOrWhiteSpace(messageId) ? WhatsAppSendResult.Unknown() : WhatsAppSendResult.Accepted(messageId);
        }

        if (status == HttpStatusCode.TooManyRequests
            || status == HttpStatusCode.ServiceUnavailable
            || (errorCode is { } code && TransientCodes.Contains(code))
            || IsAccessRefused(status, errorCode))
        {
            return WhatsAppSendResult.Transient(errorCode, errorTitle);
        }

        if (!readable && (int)status >= 500)
        {
            // A gateway or server failure without a Graph error: the request may have been processed.
            return WhatsAppSendResult.Unknown(errorCode, errorTitle);
        }

        return WhatsAppSendResult.Rejected(errorCode, errorTitle);
    }

    /// <summary>The access token or a permission of the app was refused (HTTP 401, codes 0, 10, 190, 200–299): Meta processed nothing.</summary>
    internal static bool IsAccessRefused(HttpStatusCode status, int? errorCode) =>
        status == HttpStatusCode.Unauthorized || errorCode is 0 or 10 or 190 or (>= 200 and <= 299);

    private static (string? MessageId, int? ErrorCode, string? ErrorTitle, bool Readable) Read(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return (null, null, null, false);
        }

        try
        {
            var reader = new Utf8JsonReader(body);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null, false);
            }

            string? messageId = null;
            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                messageId = messages.EnumerateArray()
                    .Select(m => m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
                    .FirstOrDefault(id => id is not null);
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                int? code = error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
                var title = error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                return (messageId, code, title, true);
            }

            return (messageId, null, null, true);
        }
        catch (JsonException)
        {
            return (null, null, null, false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WhatsApp Graph API: {Kind} message, HTTP {StatusCode}, Meta error {ErrorCode}, outcome {Outcome}, {ElapsedMs:F0} ms.")]
    private partial void LogCall(string kind, int statusCode, int? errorCode, string outcome, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp Graph API unreachable ({Reason}) for a {Kind} message: not sent.")]
    private partial void LogNotSent(Exception exception, string kind, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "WhatsApp Graph API did not answer a {Kind} message: outcome unknown.")]
    private partial void LogNoAnswer(Exception exception, string kind);

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "WhatsApp Graph API refused the access token or a permission (Meta error {ErrorCode}): renew the Key Vault secret WhatsApp--AccessToken or the permissions of the app; messages are retried meanwhile.")]
    private partial void LogAccessRefused(int? errorCode);
}
