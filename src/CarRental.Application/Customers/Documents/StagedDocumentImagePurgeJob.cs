using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Customers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Customers.Documents;

/// <summary>
/// Data minimization of the public uploads (ADR-0050): every hour, deletes the staged images never submitted after
/// <see cref="UnusedLifetime"/> (an abandoned booking funnel) and the staging records of the images already copied to a
/// person. Files first, then their records: a file left behind by a crash is found again at the next run.
/// </summary>
internal sealed partial class StagedDocumentImagePurgeJob(
    IAppDbContext db,
    DocumentImageStore store,
    TimeProvider timeProvider,
    ILogger<StagedDocumentImagePurgeJob> logger) : IRecurringJob
{
    /// <summary>A funnel session lasts 24 hours at most: a photo not submitted by then never will be.</summary>
    public static readonly TimeSpan UnusedLifetime = TimeSpan.FromHours(26);

    private const int BatchSize = 200;

    public static string JobId => "customers.purge_staged_images";

    public static string CronExpression => "17 * * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - UnusedLifetime;
        var purged = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await db.Set<StagedDocumentImage>()
                .Where(s => s.ConsumedAtUtc != null || s.Image.UploadedAtUtc < cutoff)
                .OrderBy(s => s.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var staged in batch)
            {
                await store.DeleteStagedAsync(staged.Image.Path);
            }

            db.Set<StagedDocumentImage>().RemoveRange(batch);
            await db.SaveChangesAsync(cancellationToken);
            purged += batch.Count;
            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        if (purged > 0)
        {
            LogPurged(purged);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} staged document image(s) purged.")]
    private partial void LogPurged(int count);
}
