using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Testcontainers.Azurite;
using Testcontainers.MsSql;

namespace CarRental.Testing;

/// <summary>
/// SQL Server 2022 and Azurite containers shared by every test of an assembly (xUnit assembly fixture), started in
/// parallel. Same images as docker-compose.yml. Requires a running Docker engine.
/// </summary>
/// <remarks>
/// Without Docker, set <see cref="ExternalSqlVariable"/> (a connection string to a running SQL Server 2019 or later,
/// without database, e.g. <c>Server=.\SQLEXPRESS;Integrated Security=true;TrustServerCertificate=true</c>) and
/// <see cref="ExternalStorageVariable"/> (the connection string of a running Azurite, e.g.
/// <c>UseDevelopmentStorage=true</c> for <c>npx azurite --inMemoryPersistence --skipApiVersionCheck</c>): those servers
/// are used instead. The databases then get a suffix unique to the run, so that several runs can share the server, and
/// are dropped at the end (a run that crashes leaves its <c>CarRental_*_xxxxxxxx</c> databases behind).
/// </remarks>
public sealed class InfrastructureContainers : IAsyncLifetime
{
    public const string SqlImage = "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04";
    public const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:3.37.0";

    /// <summary>Environment variable of an external SQL Server used instead of the container.</summary>
    public const string ExternalSqlVariable = "CARRENTAL_TEST_SQL";

    /// <summary>Environment variable of an external Azurite used instead of the container.</summary>
    public const string ExternalStorageVariable = "CARRENTAL_TEST_STORAGE";

    private readonly string? _externalSql = Environment.GetEnvironmentVariable(ExternalSqlVariable);
    private readonly string? _externalStorage = Environment.GetEnvironmentVariable(ExternalStorageVariable);
    private readonly string _runSuffix = "_" + Guid.NewGuid().ToString("N")[..8];
    private readonly ConcurrentDictionary<string, bool> _externalDatabases = new(StringComparer.Ordinal);
    private readonly MsSqlContainer? _sql;
    private readonly AzuriteContainer? _azurite;

    public InfrastructureContainers()
    {
        if (string.IsNullOrWhiteSpace(_externalSql) != string.IsNullOrWhiteSpace(_externalStorage))
        {
            throw new InvalidOperationException($"Set both {ExternalSqlVariable} and {ExternalStorageVariable}, or neither.");
        }

        if (!UsesExternalServers)
        {
            _sql = new MsSqlBuilder(SqlImage).Build();
            _azurite = new AzuriteBuilder(AzuriteImage)
                .WithCommand("--skipApiVersionCheck")
                .WithInMemoryPersistence()
                .Build();
        }
    }

    /// <summary>Whether the tests run on external servers instead of containers.</summary>
    public bool UsesExternalServers => !string.IsNullOrWhiteSpace(_externalSql);

    public string StorageConnectionString => _externalStorage ?? _azurite!.GetConnectionString();

    /// <summary>Connection string to <paramref name="database"/> (created by the application migrations).</summary>
    public string SqlConnectionString(string database)
    {
        var name = database;
        if (UsesExternalServers)
        {
            name = database + _runSuffix;
            _externalDatabases.TryAdd(name, true);
        }

        return new SqlConnectionStringBuilder(_externalSql ?? _sql!.GetConnectionString()) { InitialCatalog = name, TrustServerCertificate = true }.ConnectionString;
    }

    public async ValueTask InitializeAsync()
    {
        if (!UsesExternalServers)
        {
            await Task.WhenAll(_sql!.StartAsync(), _azurite!.StartAsync());
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (UsesExternalServers)
        {
            await DropExternalDatabasesAsync();
            return;
        }

        await _sql!.DisposeAsync();
        await _azurite!.DisposeAsync();
    }

    /// <summary>Drops the databases of this run on the external server (the names are generated, never user input).</summary>
    private async Task DropExternalDatabasesAsync()
    {
        SqlConnection.ClearAllPools();
        var master = new SqlConnectionStringBuilder(_externalSql) { InitialCatalog = "master", TrustServerCertificate = true }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        foreach (var name in _externalDatabases.Keys)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
            command.Parameters.AddWithValue("@name", name);
            await command.ExecuteNonQueryAsync();
        }
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

    /// <summary>Manager of the second demo agency (Sousse): never sees the main demo agency (ADR-0067).</summary>
    public const string SecondAgencyManagerEmail = "gerant.sousse@demo.local";

    /// <summary>The support of the platform: every agency, outside any agency until they enter one.</summary>
    public const string SupportEmail = "support@demo.local";
}
