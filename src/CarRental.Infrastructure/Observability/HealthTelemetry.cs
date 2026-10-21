using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CarRental.Infrastructure.Observability;

/// <summary>
/// Background evaluation of the health entries for the telemetry (ADR-0060): every registered entry, whatever its tags,
/// once a minute after a warm-up of 30 seconds, published by <see cref="HealthStatusPublisher"/>. Independent of the
/// probes of <c>/health/ready</c> (App Service, deployment): an environment without probe (recette on F1, development)
/// still reports its health.
/// </summary>
public static class HealthTelemetry
{
    /// <summary>First evaluation after start-up: migrations, seeding and the job server are running by then.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(30);

    /// <summary>One evaluation per minute, the export interval of the metrics.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    /// <summary>An evaluation that takes longer (database unreachable) reports its entries as unhealthy.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddHealthTelemetry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IHealthCheckPublisher, HealthStatusPublisher>();
        services.Configure<HealthCheckPublisherOptions>(Configure);
        return services;
    }

    /// <summary>Every entry (no predicate): an entry registered by any module is published and watched.</summary>
    public static void Configure(HealthCheckPublisherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Delay = Delay;
        options.Period = Period;
        options.Timeout = Timeout;
        options.Predicate = null;
    }
}
