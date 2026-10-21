using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.BackgroundJobs;

public static class HangfireSetup
{
    /// <summary>
    /// Hangfire on SQL Server (schema <c>hangfire</c>, Microsoft.Data.SqlClient connections so that Entra authentication
    /// works), the server in this process (single instance), the recurring job runner and the outbox dispatcher.
    /// </summary>
    public static IServiceCollection AddCarRentalHangfire(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHangfire((serviceProvider, config) =>
        {
            var database = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var jobs = serviceProvider.GetRequiredService<IOptions<BackgroundJobsOptions>>().Value;
            config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseSqlServerStorage(
                    () => new SqlConnection(database.ConnectionString),
                    new SqlServerStorageOptions
                    {
                        SchemaName = Schemas.Hangfire,
                        PrepareSchemaIfNecessary = jobs.PrepareSchemaIfNecessary,
                    });
        });

        var options = configuration.GetSection(BackgroundJobsOptions.SectionName).Get<BackgroundJobsOptions>() ?? new BackgroundJobsOptions();
        if (options.RunServer)
        {
            services.AddHangfireServer((_, server) =>
            {
                server.WorkerCount = Math.Max(1, options.WorkerCount);
                server.ServerName = $"{Environment.MachineName}:{Environment.ProcessId}";
            });

            // Commits wake an in-process pump; the every-minute job stays as a safety net (ADR-0043).
            services.AddSingleton<OutboxWakeup>();
            services.AddSingleton<IOutboxDispatcher, InProcessOutboxDispatcher>();
            services.AddHostedService<OutboxPump>();
        }
        else
        {
            // No job server here: hand the run over to the instance that processes jobs.
            services.AddSingleton<IOutboxDispatcher, HangfireOutboxDispatcher>();
        }

        services.AddScoped<RecurringJobRunner>();
        services.AddHostedService<RecurringJobsRegistrar>();
        return services;
    }
}
