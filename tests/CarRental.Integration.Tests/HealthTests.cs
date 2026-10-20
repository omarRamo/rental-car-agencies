using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CarRental.Integration.Tests;

public sealed class HealthTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_host_runs_in_the_testing_environment()
    {
        host.Factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName.ShouldBe("Testing");
    }

    [Fact]
    public async Task Liveness_is_anonymous_and_healthy()
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        report.GetProperty("status").GetString().ShouldBe("Healthy");
    }

    [Fact]
    public async Task Readiness_checks_sql_storage_and_hangfire()
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        var checks = JsonDocument.Parse(body).RootElement.GetProperty("checks");
        foreach (var name in new[] { "sql", "storage", "hangfire" })
        {
            checks.GetProperty(name).GetProperty("status").GetString().ShouldBe("Healthy", $"{name}: {body}");
        }
    }

    [Fact]
    public async Task Health_endpoints_ignore_host_filtering_and_never_redirect()
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/ready", UriKind.Relative));
        request.Headers.Host = "unknown-host.example";

        var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
