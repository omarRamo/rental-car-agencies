using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CarRental.Application.Common.Files;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Storage;

/// <summary>Creates the private containers at startup (best effort: a failure is logged, uploads create them lazily).</summary>
internal sealed partial class StorageInitializer(
    BlobServiceClient serviceClient,
    IOptions<StorageOptions> options,
    ILogger<StorageInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.CreateContainersOnStartup)
        {
            return;
        }

        try
        {
            foreach (var container in FileContainers.All)
            {
                await serviceClient.GetBlobContainerClient(container)
                    .CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
            }
        }
#pragma warning disable CA1031 // Startup continues: the readiness check reports the storage problem.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogInitializationFailed(exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Error, Message = "Blob containers could not be created at startup.")]
    private partial void LogInitializationFailed(Exception exception);
}

/// <summary>Readiness: the storage account answers with the application's credentials.</summary>
internal sealed class BlobStorageHealthCheck(BlobServiceClient serviceClient) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var exists = await serviceClient.GetBlobContainerClient(FileContainers.Documents).ExistsAsync(cancellationToken);
        return exists.Value
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded($"Container '{FileContainers.Documents}' does not exist yet.");
    }
}
