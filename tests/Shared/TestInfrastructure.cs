using Microsoft.Data.SqlClient;
using Testcontainers.Azurite;
using Testcontainers.MsSql;

namespace CarRental.Testing;

/// <summary>
/// SQL Server 2022 and Azurite containers shared by every test of an assembly (xUnit assembly fixture), started in
/// parallel. Same images as docker-compose.yml. Requires a running Docker engine.
/// </summary>
public sealed class InfrastructureContainers : IAsyncLifetime
{
    public const string SqlImage = "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04";
    public const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:3.37.0";

    private readonly MsSqlContainer _sql = new MsSqlBuilder(SqlImage).Build();

    private readonly AzuriteContainer _azurite = new AzuriteBuilder(AzuriteImage)
        .WithCommand("--skipApiVersionCheck")
        .WithInMemoryPersistence()
        .Build();

    public string StorageConnectionString => _azurite.GetConnectionString();

    /// <summary>Connection string to <paramref name="database"/> (created by the application migrations).</summary>
    public string SqlConnectionString(string database) =>
        new SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = database, TrustServerCertificate = true }.ConnectionString;

    public async ValueTask InitializeAsync() => await Task.WhenAll(_sql.StartAsync(), _azurite.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await _sql.DisposeAsync();
        await _azurite.DisposeAsync();
    }
}

/// <summary>Demo accounts seeded by the test host (Seed:Demo) with a known password and TOTP key. Test-only values.</summary>
public static class TestAccounts
{
    public const string Password = "Test-Passw0rd-2026"; // gitleaks:allow (test-only demo password)
    public const string AuthenticatorKey = "TESTTOTPKEYFORCARRENTALDEMOUSERS"; // gitleaks:allow (test-only demo TOTP key)
    public const string ManagerEmail = "gerant@demo.local";
    public const string AgentEmail = "agent@demo.local";
    public const string AccountantEmail = "comptable@demo.local";
}
