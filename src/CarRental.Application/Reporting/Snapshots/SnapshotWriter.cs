using System.Data;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Reporting.Snapshots;

/// <summary>The outcome of a computation of the snapshots of an agency.</summary>
public sealed record SnapshotRun(Guid AgencyId, DateOnly From, DateOnly To, int Days, int Rows, Money UnattributedRevenue);

/// <summary>
/// Computes the snapshots of an agency over a range of days and replaces the stored ones (ADR-0045), by chunks of
/// <see cref="ChunkDays"/> days: the inputs of a chunk are read first, outside any transaction (the reads of the other
/// modules never hold locks on their tables), then the rows of the chunk are replaced in one serializable transaction, so
/// that two computations of the same days (nightly job and a manager) follow each other instead of mixing their rows.
/// Computing a day twice gives the same rows: the operation is idempotent.
/// </summary>
internal sealed partial class SnapshotWriter(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    SnapshotInputsLoader loader,
    TimeProvider clock,
    ILogger<SnapshotWriter> logger)
{
    public const int ChunkDays = 31;

    public async Task<Result<SnapshotRun>> RecomputeAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (to < from)
        {
            return ReportingErrors.PeriodInvalid;
        }

        var rows = 0;
        var unattributed = Money.Zero;
        for (var chunkStart = from; chunkStart <= to; chunkStart = chunkStart.AddDays(ChunkDays))
        {
            var chunkEnd = chunkStart.AddDays(ChunkDays - 1) < to ? chunkStart.AddDays(ChunkDays - 1) : to;
            var inputs = await loader.LoadAsync(agencyId, chunkStart, chunkEnd, cancellationToken);
            var computed = SnapshotCalculator.Compute(agencyId, chunkStart, chunkEnd, inputs.Fleet, inputs.Rentals, inputs.Costs, clock.GetUtcNow());
            var written = await ReplaceAsync(agencyId, chunkStart, chunkEnd, computed, cancellationToken);
            if (written.IsFailure)
            {
                return written.Error;
            }

            rows += computed.Rows.Count;
            unattributed += computed.UnattributedRevenue;
        }

        LogComputed(agencyId, from, to, rows);
        return new SnapshotRun(agencyId, from, to, to.DayNumber - from.DayNumber + 1, rows, unattributed);
    }

    private async Task<Result> ReplaceAsync(Guid agencyId, DateOnly from, DateOnly to, SnapshotComputation computed, CancellationToken cancellationToken)
    {
        try
        {
            // Replayed as a whole after a transient failure: the rows computed above are added again to a cleared tracker.
            return await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    // The rentals of the deleted rows go with them (cascade of the foreign key).
                    await db.Set<VehicleDaySnapshot>()
                        .Where(s => s.AgencyId == agencyId && s.Day >= from && s.Day <= to)
                        .ExecuteDeleteAsync(ct);
                    await db.Set<SnapshotDay>()
                        .Where(d => d.AgencyId == agencyId && d.Day >= from && d.Day <= to)
                        .ExecuteDeleteAsync(ct);
                    db.Set<VehicleDaySnapshot>().AddRange(computed.Rows);
                    db.Set<SnapshotDay>().AddRange(computed.Days);
                    return Result.Success();
                },
                IsolationLevel.Serializable,
                cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // A unique index refused a row: another computation of the same days committed in between.
            LogConcurrent(exception, agencyId, from, to);
            return ReportingErrors.SnapshotConcurrentRecompute;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Snapshots of agency {AgencyId} computed from {From} to {To}: {Rows} vehicle-day(s).")]
    private partial void LogComputed(Guid agencyId, DateOnly from, DateOnly to, int rows);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Snapshots of agency {AgencyId} from {From} to {To} were computed concurrently; this computation gave way.")]
    private partial void LogConcurrent(Exception exception, Guid agencyId, DateOnly from, DateOnly to);
}
