using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CarRental.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> (migrations add, migrations bundle): needs nothing but a connection string, so the tools
/// never start the web host (no Key Vault, no Azure call). The bundle receives the real one with <c>--connection</c>.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CarRentalDbContext>
{
    /// <summary>Placeholder used when no connection string is provided (no credentials, never connected by "migrations add").</summary>
    private const string PlaceholderConnectionString = "Server=localhost,14333;Database=CarRental;Encrypt=True;TrustServerCertificate=True";

    public CarRentalDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Sql");
        var options = new DbContextOptionsBuilder<CarRentalDbContext>()
            .UseSqlServer(string.IsNullOrWhiteSpace(connectionString) ? PlaceholderConnectionString : connectionString, SqlServerSetup.Configure)
            .Options;
        return new CarRentalDbContext(options);
    }
}
