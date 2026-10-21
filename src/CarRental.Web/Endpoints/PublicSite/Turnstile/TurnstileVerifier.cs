using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Endpoints.PublicSite.Turnstile;

/// <summary>
/// Server-side check of a Turnstile token (Cloudflare <c>siteverify</c>). The booking funnel calls it before a
/// submission and before sending a magic link; a token is single-use and valid for 5 minutes.
/// </summary>
public interface ITurnstileVerifier
{
    /// <param name="token">Value of the <c>cf-turnstile-response</c> form field.</param>
    /// <param name="remoteIp">Client IP (<c>ClientIpResolver</c>), sent to Cloudflare as an extra signal.</param>
    /// <param name="expectedAction">Action declared by the widget (<c>data-action</c>), checked when given.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<TurnstileVerification> VerifyAsync(string? token, string? remoteIp, string? expectedAction = null, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a verification: success, or the error codes (Cloudflare's, or ours for local failures).</summary>
public sealed record TurnstileVerification(bool Success, IReadOnlyList<string> ErrorCodes, string? Hostname = null, string? Action = null)
{
    public const string MissingResponse = "missing-input-response";
    public const string InvalidResponse = "invalid-input-response";
    public const string MissingSecret = "missing-input-secret";
    public const string ActionMismatch = "action-mismatch";
    public const string Unavailable = "internal-error";

    public static TurnstileVerification Passed(string? hostname, string? action) => new(true, [], hostname, action);

    public static TurnstileVerification Failed(params string[] errorCodes) => new(false, errorCodes);
}

/// <summary>
/// <see cref="ITurnstileVerifier"/> over Cloudflare's siteverify endpoint (typed <see cref="HttpClient"/>). Fails
/// closed: no secret, no token, a timeout or an unexpected answer all mean "not verified".
/// </summary>
public sealed partial class TurnstileVerifier(HttpClient httpClient, IOptions<TurnstileOptions> options, ILogger<TurnstileVerifier> logger)
    : ITurnstileVerifier
{
    /// <summary>Longest token Cloudflare issues.</summary>
    public const int MaxTokenLength = 2048;

    public async Task<TurnstileVerification> VerifyAsync(string? token, string? remoteIp, string? expectedAction = null, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.SecretKey))
        {
            LogNotConfigured();
            return TurnstileVerification.Failed(TurnstileVerification.MissingSecret);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return TurnstileVerification.Failed(TurnstileVerification.MissingResponse);
        }

        if (token.Length > MaxTokenLength)
        {
            return TurnstileVerification.Failed(TurnstileVerification.InvalidResponse);
        }

        var fields = new Dictionary<string, string> { ["secret"] = settings.SecretKey, ["response"] = token };
        if (!string.IsNullOrWhiteSpace(remoteIp))
        {
            fields["remoteip"] = remoteIp;
        }

        SiteVerifyResponse? answer;
        try
        {
            using var content = new FormUrlEncodedContent(fields);
            using var response = await httpClient.PostAsync(settings.VerifyEndpoint, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogUnavailable((int)response.StatusCode);
                return TurnstileVerification.Failed(TurnstileVerification.Unavailable);
            }

            answer = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogFailed(exception);
            return TurnstileVerification.Failed(TurnstileVerification.Unavailable);
        }

        if (answer is null)
        {
            return TurnstileVerification.Failed(TurnstileVerification.Unavailable);
        }

        if (!answer.Success)
        {
            return new TurnstileVerification(false, answer.ErrorCodes ?? [], answer.Hostname, answer.Action);
        }

        return expectedAction is not null && !string.Equals(answer.Action, expectedAction, StringComparison.Ordinal)
            ? new TurnstileVerification(false, [TurnstileVerification.ActionMismatch], answer.Hostname, answer.Action)
            : TurnstileVerification.Passed(answer.Hostname, answer.Action);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Turnstile is not configured (Turnstile:SecretKey): protected submissions are refused.")]
    private partial void LogNotConfigured();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Turnstile siteverify answered HTTP {StatusCode}: the submission is not verified.")]
    private partial void LogUnavailable(int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Turnstile siteverify could not be reached: the submission is not verified.")]
    private partial void LogFailed(Exception exception);

    /// <summary>Body of a siteverify answer (only the fields used).</summary>
    private sealed record SiteVerifyResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes,
        [property: JsonPropertyName("hostname")] string? Hostname,
        [property: JsonPropertyName("action")] string? Action);
}
