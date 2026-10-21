using CarRental.Web.Endpoints.PublicSite;
using CarRental.Web.Endpoints.PublicSite.Turnstile;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CarRental.Architecture.Tests.Security;

/// <summary>
/// The answers of Cloudflare's test keys accept a token anyone can write (<c>XXXX.DUMMY.TOKEN.XXXX</c>) and report
/// neither the action nor the real host name: accepted outside production only, whatever the configuration says
/// (security review, pass 2).
/// </summary>
public sealed class TurnstileSettingsTests
{
    [Theory]
    [InlineData("Production", false)]
    [InlineData("Staging", true)]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    public void The_answers_of_test_keys_are_accepted_outside_production_only(string environment, bool accepted)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Turnstile:AcceptTestingKeys"] = "true" })
            .Build();
        using var services = new ServiceCollection()
            .AddAntiforgery()
            .AddPublicSite(configuration, new Environment(environment))
            .BuildServiceProvider();

        services.GetRequiredService<IOptions<TurnstileOptions>>().Value.AcceptTestingKeys.ShouldBe(accepted);
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CarRental.Web";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
