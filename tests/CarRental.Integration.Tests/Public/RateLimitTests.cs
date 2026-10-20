using System.Net;
using System.Text.Json;
using CarRental.Web.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using static CarRental.Integration.Tests.Public.PublicTestData;

namespace CarRental.Integration.Tests.Public;

/// <summary>
/// Rate limiting of the public endpoints on a dedicated host with a low limit (3 requests per window and per client),
/// on the shared database. Each scenario uses its own client address (CF-Connecting-IP), hence its own partition.
/// </summary>
[Collection(PublicScenarioCollection.Name)]
public sealed class RateLimitTests(PublicScenarioHost scenario)
{
    private const int Limit = PublicScenarioHost.RateLimit;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client() => scenario.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, string clientIp)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Add(ClientIpResolver.CloudflareHeader, clientIp);
        request.Headers.Add("Accept-Language", "fr");
        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Searches_beyond_the_limit_get_a_friendly_localized_page_and_the_health_probes_never_do()
    {
        using var client = Client();
        var url = $"/fr/reserver?lieu=TUN&depart={LocalDate(70)}&retour={LocalDate(73)}";
        const string ip = "198.51.100.10";

        for (var attempt = 0; attempt < Limit; attempt++)
        {
            (await GetAsync(client, url, ip)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var rejected = await GetAsync(client, url, ip);
        var home = await GetAsync(client, "/fr", ip);
        var otherClient = await GetAsync(client, url, "198.51.100.11");
        var health = await GetAsync(client, "/health", ip);
        var ready = await GetAsync(client, "/health/ready", ip);

        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.ShouldNotBeNull();
        rejected.Headers.CacheControl!.NoStore.ShouldBeTrue();
        rejected.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        var page = WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync(Ct));
        page.ShouldContain("<html lang=\"fr\"");
        page.ShouldContain("Trop de demandes");
        page.ShouldContain("Merci de patienter une minute");
        home.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "The home page shares the public-search policy.");
        otherClient.StatusCode.ShouldBe(HttpStatusCode.OK);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_api_answers_429_with_a_problem_details()
    {
        using var client = Client();
        var url = $"/api/public/availability?lieu=TUN&depart={LocalDate(70)}&retour={LocalDate(73)}";
        const string ip = "2001:db8:42:1::1";

        for (var attempt = 0; attempt < Limit; attempt++)
        {
            (await GetAsync(client, url, ip)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Same /64 network: same client.
        var rejected = await GetAsync(client, url, "2001:db8:42:1::ffff");

        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        rejected.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync(Ct)).RootElement;
        problem.GetProperty("status").GetInt32().ShouldBe(429);
        problem.GetProperty("title").GetString().ShouldBe("Trop de demandes");
        rejected.Headers.RetryAfter!.Delta!.Value.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task Static_pages_and_the_back_office_are_not_rate_limited()
    {
        using var client = Client();
        const string ip = "198.51.100.20";

        for (var attempt = 0; attempt < Limit + 2; attempt++)
        {
            (await GetAsync(client, "/fr/faq", ip)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await GetAsync(client, "/account/login", ip)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }
}
