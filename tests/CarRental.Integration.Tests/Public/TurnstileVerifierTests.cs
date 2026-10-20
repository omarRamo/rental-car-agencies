using System.Net;
using System.Text;
using CarRental.Web.Endpoints.PublicSite.Turnstile;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CarRental.Integration.Tests.Public;

/// <summary>Server-side Turnstile verification against a stubbed siteverify endpoint (no call to Cloudflare).</summary>
public sealed class TurnstileVerifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (TurnstileVerifier Verifier, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond, string? secret = TurnstileTestKeys.AlwaysPassesSecretKey)
    {
        var handler = new StubHandler(respond);
        var options = Options.Create(new TurnstileOptions { SiteKey = TurnstileTestKeys.AlwaysPassesSiteKey, SecretKey = secret });
        return (new TurnstileVerifier(new HttpClient(handler), options, NullLogger<TurnstileVerifier>.Instance), handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_valid_token_is_verified_with_the_secret_and_the_client_address()
    {
        var (verifier, handler) = Create(_ => Json("""{"success":true,"error-codes":[],"hostname":"www.example.tn","action":"booking_submit"}"""));

        var result = await verifier.VerifyAsync("token-123", "203.0.113.9", "booking_submit", Ct);

        result.Success.ShouldBeTrue();
        result.Hostname.ShouldBe("www.example.tn");
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri("https://challenges.cloudflare.com/turnstile/v0/siteverify"));
        request.Body.ShouldContain($"secret={TurnstileTestKeys.AlwaysPassesSecretKey}");
        request.Body.ShouldContain("response=token-123");
        request.Body.ShouldContain("remoteip=203.0.113.9");
    }

    [Fact]
    public async Task A_refused_token_returns_the_error_codes_of_cloudflare()
    {
        var (verifier, _) = Create(_ => Json("""{"success":false,"error-codes":["timeout-or-duplicate"]}"""));

        var result = await verifier.VerifyAsync("used-token", null, cancellationToken: Ct);

        result.Success.ShouldBeFalse();
        result.ErrorCodes.ShouldBe(["timeout-or-duplicate"]);
    }

    [Fact]
    public async Task A_token_issued_for_another_action_is_refused()
    {
        var (verifier, _) = Create(_ => Json("""{"success":true,"action":"magic_link"}"""));

        var result = await verifier.VerifyAsync("token", null, "booking_submit", Ct);

        result.Success.ShouldBeFalse();
        result.ErrorCodes.ShouldBe([TurnstileVerification.ActionMismatch]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task A_missing_token_is_refused_without_calling_cloudflare(string? token)
    {
        var (verifier, handler) = Create(_ => throw new InvalidOperationException("No call expected."));

        var result = await verifier.VerifyAsync(token, null, cancellationToken: Ct);

        result.ErrorCodes.ShouldBe([TurnstileVerification.MissingResponse]);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_oversized_token_is_refused_without_calling_cloudflare()
    {
        var (verifier, handler) = Create(_ => throw new InvalidOperationException("No call expected."));

        var result = await verifier.VerifyAsync(new string('x', TurnstileVerifier.MaxTokenLength + 1), null, cancellationToken: Ct);

        result.ErrorCodes.ShouldBe([TurnstileVerification.InvalidResponse]);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_secret_every_token_is_refused()
    {
        var (verifier, handler) = Create(_ => Json("""{"success":true}"""), secret: null);

        var result = await verifier.VerifyAsync("token", null, cancellationToken: Ct);

        result.Success.ShouldBeFalse();
        result.ErrorCodes.ShouldBe([TurnstileVerification.MissingSecret]);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unavailable_or_garbled_answer_fails_closed()
    {
        var (down, _) = Create(_ => Json("{}", HttpStatusCode.ServiceUnavailable));
        var (garbled, _) = Create(_ => Json("<html>oops</html>"));
        var (unreachable, _) = Create(_ => throw new HttpRequestException("Connection refused."));
        var (slow, _) = Create(_ => throw new TaskCanceledException("Timed out."));

        (await down.VerifyAsync("token", null, cancellationToken: Ct)).ErrorCodes.ShouldBe([TurnstileVerification.Unavailable]);
        (await garbled.VerifyAsync("token", null, cancellationToken: Ct)).ErrorCodes.ShouldBe([TurnstileVerification.Unavailable]);
        (await unreachable.VerifyAsync("token", null, cancellationToken: Ct)).ErrorCodes.ShouldBe([TurnstileVerification.Unavailable]);
        (await slow.VerifyAsync("token", null, cancellationToken: Ct)).ErrorCodes.ShouldBe([TurnstileVerification.Unavailable]);
    }

    [Fact]
    public async Task The_fake_of_the_test_hosts_accepts_the_offline_dummy_token()
    {
        var fake = new CarRental.Testing.FakeTurnstileVerifier();

        (await fake.VerifyAsync(TurnstileTestKeys.DummyToken, null, "booking_submit", Ct)).Success.ShouldBeTrue();
        (await fake.VerifyAsync(CarRental.Testing.FakeTurnstileVerifier.RejectedToken, null, cancellationToken: Ct)).Success.ShouldBeFalse();
        (await fake.VerifyAsync(null, null, cancellationToken: Ct)).Success.ShouldBeFalse();
        fake.Calls.Count.ShouldBe(3);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri? Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri, body));
            return response;
        }
    }
}
