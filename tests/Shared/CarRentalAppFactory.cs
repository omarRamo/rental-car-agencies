using CarRental.Application.Referential.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarRental.Testing;

/// <summary>
/// The real application (Program) in the <c>Testing</c> environment against the shared containers: migrations applied
/// at startup, demo data seeded with <see cref="TestAccounts"/>, e-mails written to <see cref="EmailDirectory"/>.
/// Use <see cref="CreateKestrel"/> for browser tests (real HTTP server on a free port).
/// </summary>
public sealed class CarRentalAppFactory : WebApplicationFactory<Program>
{
    private readonly InfrastructureContainers _containers;
    private readonly string _database;
    private readonly Action<IServiceCollection>? _configureServices;

    public CarRentalAppFactory(InfrastructureContainers containers, string database, Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(containers);
        _containers = containers;
        _database = database;
        _configureServices = configureServices;
        var root = Path.Combine(Path.GetTempPath(), "carrental-tests", $"{database}-{Guid.NewGuid():N}");
        EmailDirectory = Path.Combine(root, "mail");
        KeysDirectory = Path.Combine(root, "keys");
    }

    /// <summary>Key of the public quote API in the test hosts (test-only value).</summary>
    public const string QuoteApiKey = "test-quote-api-key-2c"; // gitleaks:allow (test-only API key)

    /// <summary>Folder of the .eml files written by the development e-mail transport.</summary>
    public string EmailDirectory { get; }

    public string KeysDirectory { get; }

    /// <summary>Base address of the Kestrel server (after <see cref="CreateKestrel"/>).</summary>
    public Uri? ServerAddress { get; private set; }

    /// <summary>Permits of each public rate limiting policy (lifted by default: all tests share one client address).</summary>
    public int RateLimitPermits { get; init; } = 100_000;

    /// <summary>Configuration applied after the defaults of the test hosts (a dedicated host: online payment off…).</summary>
    public IReadOnlyDictionary<string, string?> ExtraSettings { get; init; } = new Dictionary<string, string?>();

    /// <summary>Permits of the account forms policy (lifted by default: every test signs in from the same address).</summary>
    public int AccountRateLimitPermits { get; init; } = 100_000;

    /// <summary>Permits of the health probe throttle (lifted by default).</summary>
    public int HealthRateLimitPermits { get; init; } = 100_000;

    /// <summary>Absolute lifetime of the staff sessions (<c>Identity:Session:AbsoluteLifetime</c>), when a test needs a short one.</summary>
    public TimeSpan? SessionAbsoluteLifetime { get; init; }

    /// <summary>Turnstile verifier of the host: accepts any token except <see cref="FakeTurnstileVerifier.RejectedToken"/>.</summary>
    public FakeTurnstileVerifier TurnstileVerifier { get; } = new();

    /// <summary>
    /// WhatsApp of the host (<c>WhatsApp:Mode=Fake</c>): records the messages sent, simulates customers and statuses. The
    /// webhook verifies signatures with <see cref="CarRental.Infrastructure.Messaging.WhatsAppDevelopmentKeys.AppSecret"/>.
    /// </summary>
    public CarRental.Infrastructure.Messaging.FakeWhatsApp WhatsApp => Services.GetRequiredService<CarRental.Infrastructure.Messaging.FakeWhatsApp>();

    /// <summary>
    /// The fake model of the WhatsApp agent (<c>Ai:Mode=Fake</c>): the bot answers only the numbers a test scripts on it
    /// (<see cref="ScriptedAgentAudience"/>), through the bot turn executor of the host.
    /// </summary>
    public CarRental.Infrastructure.Messaging.Agent.ScriptedChatClient Agent => Services.GetRequiredService<CarRental.Infrastructure.Messaging.Agent.ScriptedChatClient>();

    public string SqlConnectionString => _containers.SqlConnectionString(_database);

    /// <summary>Starts the application on Kestrel (free port) and returns its base address.</summary>
    public Uri CreateKestrel()
    {
        UseKestrel(0);
        StartServer();
        var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        ServerAddress = new Uri(addresses.First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal).Replace("localhost", "127.0.0.1", StringComparison.Ordinal));
        return ServerAddress;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");

        // Serves library assets (_content/MudBlazor/…) from the build output like in Development; published builds copy them to wwwroot.
        builder.UseStaticWebAssets();
        builder.UseSetting("ConnectionStrings:Sql", SqlConnectionString);
        builder.UseSetting("Storage:ConnectionString", _containers.StorageConnectionString);
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
        builder.UseSetting("Seed:Demo", "true");
        builder.UseSetting("Seed:DemoUserPassword", TestAccounts.Password);
        builder.UseSetting("Seed:DemoAuthenticatorKey", TestAccounts.AuthenticatorKey);
        builder.UseSetting("Email:PickupDirectory", EmailDirectory);
        builder.UseSetting("DataProtection:KeysDirectory", KeysDirectory);
        builder.UseSetting("HostFiltering:AllowedHosts", "*");
        builder.UseSetting("PublicSite:WhatsAppNumber", "21600000000");
        builder.UseSetting("PublicApi:QuoteApiKeys:0", QuoteApiKey);
        builder.UseSetting("BackgroundJobs:WorkerCount", "2");
        builder.ConfigureTestServices(services => services.AddSettingDefinitions(TestSettings.All));

        // Every test of the run shares one client address: the public rate limits are lifted for the shared hosts
        // (a dedicated host checks them with low limits), and Turnstile is offline with an accepting fake verifier.
        foreach (var policy in new[] { "Search", "Submit", "Upload", "Media" })
        {
            builder.UseSetting($"RateLimiting:{policy}:PermitLimit", RateLimitPermits.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        builder.UseSetting("RateLimiting:Account:PermitLimit", AccountRateLimitPermits.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("RateLimiting:Health:PermitLimit", HealthRateLimitPermits.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (SessionAbsoluteLifetime is { } lifetime)
        {
            builder.UseSetting("Identity:Session:AbsoluteLifetime", lifetime.ToString("c", System.Globalization.CultureInfo.InvariantCulture));
        }

        // Tests stand for distinct clients with CF-Connecting-IP, as Cloudflare does in front of the origin it is
        // reserved to (ReverseProxy:TrustCloudflareHeader is off by default, ADR-0062).
        builder.UseSetting("ReverseProxy:TrustCloudflareHeader", "true");

        // Channels: same lifted (or low) limit for the inbound endpoint, a short debounce so that the pump sends updates
        // at once, and the App Service certificate header so that the tests can present a client certificate.
        builder.UseSetting("Channels:RateLimiting:PermitLimit", RateLimitPermits.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Channels:Sync:Debounce", "00:00:00.100");
        builder.UseSetting("Channels:Inbound:TrustArrClientCertHeader", "true");

        builder.UseSetting("Turnstile:OfflineTestWidget", "true");

        // WhatsApp without Meta: the fake records the sends and simulates the statuses (development keys of the webhook).
        // The number serves the demo agency (ADR-0067), whatever other agencies of the run switch their module on; the
        // hosts switch its WhatsApp module on (TestModules), a dedicated host may route elsewhere.
        builder.UseSetting("WhatsApp:Mode", "Fake");
        builder.UseSetting("WhatsApp:AgencySlug", TestModules.DemoAgencySlug);
        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<CarRental.Web.Endpoints.PublicSite.Turnstile.ITurnstileVerifier>(TurnstileVerifier)));

        // The WhatsApp agent on its scripted model, for the numbers a test scripts only; short bot turn delays.
        builder.UseSetting("Ai:Mode", "Fake");
        builder.UseSetting("BotTurns:MaxParallelism", "8");
        builder.UseSetting("BotTurns:QuietPeriod", "00:00:00.300");
        builder.UseSetting("BotTurns:SweepInterval", "00:00:02");
        builder.UseSetting("BotTurns:RetryDelays", "00:00:01;00:00:02");
        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<CarRental.Application.Messaging.Agent.Turns.IBotAudience, ScriptedAgentAudience>()));

        // Simulated Clic to Pay gateway (FakeClicToPay, pages under /dev/clictopay) with its status codes beyond 0-2.
        builder.UseSetting("ClicToPay:Enabled", "true");
        builder.UseSetting("ClicToPay:UseFake", "true");
        builder.UseSetting("ClicToPay:ReversedStatusCodes", "3");
        builder.UseSetting("ClicToPay:RefundedStatusCodes", "4");
        builder.UseSetting("ClicToPay:DeclinedStatusCodes", "6");
        builder.UseSetting("ClicToPay:StatusRetryDelayMilliseconds", "0");

        // Electronic invoicing: an agency switched to TTN (billing.einvoice_ttn) transmits to the fake (TTN-FAKE-…
        // references), never to the real adapter, which is not configured until the TEIF specification.
        builder.UseSetting("Billing:Ttn:UseFake", "true");

        // The extra settings of a dedicated host come last: they override any default above.
        foreach (var (key, value) in ExtraSettings)
        {
            builder.UseSetting(key, value);
        }

        if (_configureServices is not null)
        {
            builder.ConfigureTestServices(_configureServices);
        }
    }
}
