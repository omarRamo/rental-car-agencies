using CarRental.Application.Common.Security;
using CarRental.Application.Common.Seeding;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Seeding;

/// <summary>
/// Startup initialization, run by the host before it starts listening: migrations (when enabled), then the reference
/// seed steps, then the demo steps when <c>Seed:Demo</c> is on. Each step runs in its own scope as <c>system:seed</c>.
/// </summary>
public sealed partial class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    IOptions<DatabaseOptions> databaseOptions,
    IOptions<SeedOptions> seedOptions,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (databaseOptions.Value.ApplyMigrationsOnStartup)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            LogMigrating();
            await db.Database.MigrateAsync(cancellationToken);
        }

        await RunStageAsync(SeedStage.Reference, cancellationToken);
        if (seedOptions.Value.Demo)
        {
            await RunStageAsync(SeedStage.Demo, cancellationToken);
        }
    }

    private async Task RunStageAsync(SeedStage stage, CancellationToken cancellationToken)
    {
        List<Type> steps;
        await using (var discovery = scopeFactory.CreateAsyncScope())
        {
            steps = discovery.ServiceProvider.GetServices<IDataSeeder>()
                .Where(s => s.Stage == stage)
                .OrderBy(s => s.Order)
                .Select(s => s.GetType())
                .ToList();
        }

        foreach (var stepType in steps)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:seed"));
            var step = scope.ServiceProvider.GetServices<IDataSeeder>().First(s => s.GetType() == stepType);
            LogSeeding(stage, stepType.Name);
            await step.SeedAsync(cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying database migrations.")]
    private partial void LogMigrating();

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed step {Stage}/{Step}.")]
    private partial void LogSeeding(SeedStage stage, string step);
}
