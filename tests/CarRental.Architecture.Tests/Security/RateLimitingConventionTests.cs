using System.Reflection;
using System.Text.Json;
using CarRental.Web.Endpoints.PublicSite;
using CarRental.Web.Hosting;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CarRental.Architecture.Tests.Security;

/// <summary>
/// Anti-automation of the anonymous surfaces (ADR-0062): the account forms, the health probes and the client address
/// they are counted for.
/// </summary>
public sealed class RateLimitingConventionTests
{
    [Fact]
    public void Every_account_page_is_under_the_account_policy()
    {
        var pages = Assemblies.Web.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t) && t.GetCustomAttributes<RouteAttribute>().Any())
            .Where(t => t.Namespace!.StartsWith("CarRental.Web.Components.Account", StringComparison.Ordinal))
            .ToList();

        pages.ShouldNotBeEmpty();
        pages.ShouldAllBe(page => page.GetCustomAttributes<EnableRateLimitingAttribute>().Any(a => a.PolicyName == SecurityRateLimits.Account));
    }

    [Fact]
    public void Account_forms_are_counted_per_client_and_reading_a_page_is_not()
    {
        var services = Services(trustCloudflare: true);

        var read = SecurityRateLimiting.AccountPartition(Request(services, HttpMethods.Get, "198.51.100.7"));
        var submit = SecurityRateLimiting.AccountPartition(Request(services, HttpMethods.Post, "198.51.100.7"));
        var neighbour = SecurityRateLimiting.AccountPartition(Request(services, HttpMethods.Post, "2001:db8:5:1::7"));

        read.PartitionKey.ShouldBe("account:read");
        submit.PartitionKey.ShouldBe("account:198.51.100.7");
        neighbour.PartitionKey.ShouldBe("account:2001:db8:5:1::/64");
    }

    [Fact]
    public async Task Health_probes_beyond_the_limit_of_an_address_get_429_and_the_others_go_on()
    {
        var services = Services(trustCloudflare: true, healthPermits: 2);
        var app = new ApplicationBuilder(services);
        app.UseHealthProbeThrottling();
        app.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        var pipeline = app.Build();

        async Task<HttpContext> SendAsync(string path, string ip)
        {
            var context = Request(services, HttpMethods.Get, ip, path);
            context.Response.Body = new MemoryStream();
            await pipeline(context);
            return context;
        }

        (await SendAsync("/health", "203.0.113.9")).Response.StatusCode.ShouldBe(200);
        (await SendAsync("/health/ready", "203.0.113.9")).Response.StatusCode.ShouldBe(200);
        var throttled = await SendAsync("/health/ready", "203.0.113.9");
        var other = await SendAsync("/health/ready", "203.0.113.10");
        var page = await SendAsync("/fr", "203.0.113.9");

        throttled.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        throttled.Response.Headers.RetryAfter.ToString().ShouldNotBeEmpty();
        throttled.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
        other.Response.StatusCode.ShouldBe(200);
        page.Response.StatusCode.ShouldBe(200, "only the probes go through this throttle");
    }

    [Fact]
    public void The_default_limits_leave_room_for_the_legitimate_callers()
    {
        var limits = new SecurityRateLimitOptions();

        limits.Health.PermitLimit.ShouldBeGreaterThanOrEqualTo(30, "App Service, the deployment check and a monitor probe every minute");
        limits.Account.PermitLimit.ShouldBeGreaterThanOrEqualTo(20, "ten staff members sign in from one office (password and code)");
        new[] { limits.Account, limits.Health, limits.Media }.ShouldAllBe(l => SecurityRateLimitOptions.IsValid(l));
    }

    [Fact]
    public void Cf_connecting_ip_is_not_trusted_unless_the_deployment_says_so()
    {
        new ReverseProxyOptions().TrustCloudflareHeader.ShouldBeFalse();

        using var settings = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.PathOf("src", "CarRental.Web", "appsettings.json")));
        settings.RootElement.GetProperty("ReverseProxy").GetProperty("TrustCloudflareHeader").GetBoolean().ShouldBeFalse();

        var untrusted = new ClientIpResolver(Options.Create(new ReverseProxyOptions()));
        var context = new DefaultHttpContext();
        context.Request.Headers[ClientIpResolver.CloudflareHeader] = "203.0.113.200";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.1");
        untrusted.Resolve(context).ShouldBe("198.51.100.1");
    }

    [Fact]
    public void The_infrastructure_trusts_the_header_exactly_when_the_origin_is_reserved_to_cloudflare()
    {
        var main = File.ReadAllText(RepositoryFiles.PathOf("infra", "main.bicep"));

        main.ShouldContain("ReverseProxy__TrustCloudflareHeader: restrictToCloudflare ? 'true' : 'false'");
        main.ShouldContain("param restrictToCloudflare bool = false");
    }

    private static ServiceProvider Services(bool trustCloudflare, int healthPermits = 60) =>
        new ServiceCollection()
            .AddLogging()
            .AddRateLimiter(_ => { })
            .AddSingleton(Options.Create(new ReverseProxyOptions { TrustCloudflareHeader = trustCloudflare }))
            .AddSingleton<ClientIpResolver>()
            .AddSingleton(Options.Create(new SecurityRateLimitOptions
            {
                Health = new WindowLimit { PermitLimit = healthPermits, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 1 },
            }))
            .BuildServiceProvider();

    private static DefaultHttpContext Request(IServiceProvider services, string method, string ip, string path = "/account/login")
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers[ClientIpResolver.CloudflareHeader] = ip;
        return context;
    }
}
