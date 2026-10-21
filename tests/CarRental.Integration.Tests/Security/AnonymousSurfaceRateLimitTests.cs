using System.Net;
using CarRental.Integration.Tests.Support;

namespace CarRental.Integration.Tests.Security;

/// <summary>
/// Rate limits added by the security review (ADR-0062) through the real pipeline, with low limits
/// (<see cref="SecurityScenarioHost.Limit"/> per window and per client address): account form submissions, health probes
/// and anonymous category photos. Each scenario has its own client address.
/// </summary>
[Collection(SecurityScenarioCollection.Name)]
public sealed class AnonymousSurfaceRateLimitTests(SecurityScenarioHost scenario)
{
    private const int Limit = SecurityScenarioHost.Limit;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sign_in_attempts_beyond_the_limit_of_an_address_get_429_while_the_form_still_displays()
    {
        using var client = scenario.CreateClient("198.51.100.61");
        using var other = scenario.CreateClient("198.51.100.62");

        for (var attempt = 0; attempt < Limit; attempt++)
        {
            (await PostLoginAsync(client)).StatusCode.ShouldBe(HttpStatusCode.OK, "a wrong password shows the form again");
        }

        var rejected = await PostLoginAsync(client);
        var page = await client.GetAsync(new Uri("/account/login", UriKind.Relative), Ct);
        var otherClient = await PostLoginAsync(other);

        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.ShouldNotBeNull();
        rejected.Headers.CacheControl!.NoStore.ShouldBeTrue();
        WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync(Ct)).ShouldContain("Trop de demandes");
        page.StatusCode.ShouldBe(HttpStatusCode.OK, "displaying a form is not counted");
        otherClient.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_forms_of_the_account_pages_share_one_budget_per_address()
    {
        using var client = scenario.CreateClient("198.51.100.63");

        for (var attempt = 0; attempt < Limit; attempt++)
        {
            (await PostForgotPasswordAsync(client)).StatusCode.ShouldBe(HttpStatusCode.Redirect, "the same answer whether the account exists or not");
        }

        (await PostForgotPasswordAsync(client)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "no reset e-mail sent in a loop");
        (await PostLoginAsync(client)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Health_probes_beyond_the_limit_of_an_address_get_a_plain_429()
    {
        using var client = scenario.CreateClient("198.51.100.64");
        using var probe = scenario.CreateClient("198.51.100.65");

        for (var attempt = 0; attempt < Limit; attempt++)
        {
            (await client.GetAsync(new Uri(attempt % 2 == 0 ? "/health" : "/health/ready", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var rejected = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
        var other = await probe.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);

        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        rejected.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        rejected.Headers.CacheControl!.NoStore.ShouldBeTrue();
        other.StatusCode.ShouldBe(HttpStatusCode.OK, "another address (App Service, the deployment) is not affected");
    }

    [Fact]
    public async Task Category_photos_beyond_the_limit_of_an_address_get_429()
    {
        using var client = scenario.CreateClient("198.51.100.66");
        var photo = new Uri($"/media/categories/{Guid.CreateVersion7()}", UriKind.Relative);

        for (var attempt = 0; attempt < Limit; attempt++)
        {
            (await client.GetAsync(photo, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "an unknown category has no photo");
        }

        (await client.GetAsync(photo, Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client)
    {
        var page = await client.GetStringAsync(new Uri("/account/login", UriKind.Relative), Ct);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpSignIn.AntiforgeryToken(page),
            ["_handler"] = "login",
            ["Input.Email"] = $"nobody-{Guid.NewGuid():N}@tests.local",
            ["Input.Password"] = "Not-The-Password-2026",
        });
        return await client.PostAsync(new Uri("/account/login", UriKind.Relative), form, Ct);
    }

    private static async Task<HttpResponseMessage> PostForgotPasswordAsync(HttpClient client)
    {
        var page = await client.GetStringAsync(new Uri("/account/forgot-password", UriKind.Relative), Ct);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpSignIn.AntiforgeryToken(page),
            ["_handler"] = "forgot-password",
            ["Input.Email"] = $"nobody-{Guid.NewGuid():N}@tests.local",
        });
        return await client.PostAsync(new Uri("/account/forgot-password", UriKind.Relative), form, Ct);
    }
}
