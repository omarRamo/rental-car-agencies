namespace CarRental.Infrastructure.BackgroundJobs;

public sealed class BackgroundJobsOptions
{
    public const string SectionName = "BackgroundJobs";

    /// <summary>Runs the Hangfire server in this process (single App Service instance). Storage and dashboard work either way.</summary>
    public bool RunServer { get; set; } = true;

    public int WorkerCount { get; set; } = 4;

    /// <summary>Lets Hangfire create and upgrade its <c>hangfire</c> schema at startup (the app identity has db_ddladmin).</summary>
    public bool PrepareSchemaIfNecessary { get; set; } = true;
}
