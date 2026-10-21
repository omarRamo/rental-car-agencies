using System.Globalization;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Security;
using CarRental.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarRental.Integration.Tests.Scenarios;

/// <summary>
/// The host of the high-season day (docs/scenarios/journee-haute-saison.md): the real application on a database of its
/// own, whose clock the scenario moves through the day (<see cref="ScenarioClock"/>), without the job server — the expiry
/// and outbox jobs run when the scenario runs them, so that nothing changes the day behind its back — and without the
/// demo data: the database holds the agency of the day only.
/// </summary>
/// <remarks>
/// A database of its own, like the other scenario hosts: on the shared one, its clock would decide the expiries and
/// the "now" of the other tests' data, and their jobs would run on its files.
/// </remarks>
public sealed class HighSeasonHost : IAsyncLifetime
{
    public const string Database = "CarRental_HighSeason";

    public HighSeasonHost(IntegrationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Clock = new ScenarioClock();
        Factory = new CarRentalAppFactory(host.Containers, Database, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        })
        {
            ExtraSettings = new Dictionary<string, string?>
            {
                ["BackgroundJobs:RunServer"] = "false",
                ["Seed:Demo"] = "false",
            },
        };
    }

    public ScenarioClock Clock { get; }

    public CarRentalAppFactory Factory { get; }

    public ValueTask InitializeAsync()
    {
        // Starts the host: migrations and reference data.
        _ = Factory.Server;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();

    /// <summary>A DI scope acting as <paramref name="actor"/>, like a request of the back office or a job.</summary>
    public AsyncServiceScope CreateScope(UserContextSnapshot actor)
    {
        var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(actor);
        return scope;
    }

    /// <summary>Runs a call of a service in its own scope, as each operation of the back office does.</summary>
    public async Task<T> RunAsync<TService, T>(UserContextSnapshot actor, Func<TService, Task<T>> operation)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var scope = CreateScope(actor);
        return await operation(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>Runs a recurring job once, as Hangfire would at its time.</summary>
    public async Task RunJobAsync(string jobId, CancellationToken cancellationToken)
    {
        await using var scope = CreateScope(UserContextSnapshot.System(string.Create(CultureInfo.InvariantCulture, $"system:job:{jobId}")));
        var descriptor = scope.ServiceProvider.GetServices<RecurringJobDescriptor>().Single(d => d.JobId == jobId);
        await ((IRecurringJob)scope.ServiceProvider.GetRequiredService(descriptor.JobType)).ExecuteAsync(cancellationToken);
    }
}

/// <summary>The classes that play on the <see cref="HighSeasonHost"/>.</summary>
[CollectionDefinition(Name)]
public sealed class HighSeasonCollection : ICollectionFixture<HighSeasonHost>
{
    public const string Name = "High-season day";
}
