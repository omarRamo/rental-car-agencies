using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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

        // Simulated Clic to Pay gateway (FakeClicToPay, pages under /dev/clictopay) with its status codes beyond 0-2.
        builder.UseSetting("ClicToPay:Enabled", "true");
        builder.UseSetting("ClicToPay:UseFake", "true");
        builder.UseSetting("ClicToPay:ReversedStatusCodes", "3");
        builder.UseSetting("ClicToPay:RefundedStatusCodes", "4");
        builder.UseSetting("ClicToPay:DeclinedStatusCodes", "6");
        builder.UseSetting("ClicToPay:StatusRetryDelayMilliseconds", "0");


        if (_configureServices is not null)
        {
            builder.ConfigureTestServices(_configureServices);
        }
    }
}
