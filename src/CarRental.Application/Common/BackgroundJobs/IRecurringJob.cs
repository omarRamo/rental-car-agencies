using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Common.BackgroundJobs;

/// <summary>
/// A job run on a schedule by the background job server (Hangfire), in its own DI scope with the system actor.
/// Runs of the same job never overlap: a run that finds the previous one still running is skipped.
/// </summary>
/// <remarks>Register with <see cref="RecurringJobServiceCollectionExtensions.AddRecurringJob{TJob}"/>.</remarks>
public interface IRecurringJob
{
    /// <summary>Stable identifier, e.g. <c>bookings.expire_holds</c> (renaming it creates a new schedule).</summary>
    static abstract string JobId { get; }

    /// <summary>Cron expression, evaluated in the Africa/Tunis time zone (e.g. <c>*/15 * * * *</c>).</summary>
    static abstract string CronExpression { get; }

    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>A registered recurring job.</summary>
public sealed record RecurringJobDescriptor(string JobId, string CronExpression, Type JobType);

public static class RecurringJobServiceCollectionExtensions
{
    public static IServiceCollection AddRecurringJob<TJob>(this IServiceCollection services)
        where TJob : class, IRecurringJob
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<TJob>();
        services.AddSingleton(new RecurringJobDescriptor(TJob.JobId, TJob.CronExpression, typeof(TJob)));
        return services;
    }
}
