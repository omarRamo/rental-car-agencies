using System.Collections.Concurrent;
using CarRental.Web.Endpoints.PublicSite.Turnstile;

namespace CarRental.Testing;

/// <summary>
/// Turnstile verifier of the test hosts (no call to Cloudflare): accepts every non-empty token except
/// <see cref="RejectedToken"/>, and records the calls. The offline widget of the tests posts
/// <see cref="TurnstileTestKeys.DummyToken"/>.
/// </summary>
public sealed class FakeTurnstileVerifier : ITurnstileVerifier
{
    /// <summary>A token the fake refuses, to test the "robot" path of a form.</summary>
    public const string RejectedToken = "rejected-by-test";

    public ConcurrentQueue<(string? Token, string? RemoteIp, string? Action)> Calls { get; } = new();

    public Task<TurnstileVerification> VerifyAsync(string? token, string? remoteIp, string? expectedAction = null, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue((token, remoteIp, expectedAction));
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(TurnstileVerification.Failed(TurnstileVerification.MissingResponse));
        }

        return Task.FromResult(token == RejectedToken
            ? TurnstileVerification.Failed("invalid-input-response")
            : TurnstileVerification.Passed("localhost", expectedAction));
    }
}
