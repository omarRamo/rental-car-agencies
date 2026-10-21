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

    /// <summary>Folder of the .eml files written by the development e-mail transport.</summary>
    public string EmailDirectory { get; }

    public string KeysDirectory { get; }

    /// <summary>Base address of the Kestrel server (after <see cref="CreateKestrel"/>).</summary>
    public Uri? ServerAddress { get; private set; }

    /// <summary>Permits of each public rate limiting policy (lifted by default: all tests share one client address).</summary>
    public int RateLimitPermits { get; init; } = 100_000;

    /// <summary>Turnstile verifier of the host: accepts any token except <see cref="FakeTurnstileVerifier.RejectedToken"/>.</summary>
    public FakeTurnstileVerifier TurnstileVerifier { get; } = new();

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
        builder.UseSetting("BackgroundJobs:WorkerCount", "2");
        builder.ConfigureTestServices(services => services.AddSettingDefinitions(TestSettings.All));

        // Every test of the run shares one client address: the public rate limits are lifted for the shared hosts
        // (a dedicated host checks them with low limits), and Turnstile is offline with an accepting fake verifier.
        foreach (var policy in new[] { "Search", "Submit", "Upload" })
        {
            builder.UseSetting($"RateLimiting:{policy}:PermitLimit", RateLimitPermits.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        builder.UseSetting("Turnstile:OfflineTestWidget", "true");
        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<CarRental.Web.Endpoints.PublicSite.Turnstile.ITurnstileVerifier>(TurnstileVerifier)));

        if (_configureServices is not null)
        {
            builder.ConfigureTestServices(_configureServices);
        }
    }
}
