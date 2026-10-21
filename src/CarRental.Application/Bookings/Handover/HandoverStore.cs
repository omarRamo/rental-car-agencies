using CarRental.Application.Common.Files;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Bookings.Handover;

/// <summary>
/// Inspections of the module: loads (with their photos, damages, signatures and charges) and the write pipeline of a
/// draft — one unit of work per change (replayable: the inspection and its file are loaded inside), the hand-over
/// permission, a file whose status still allows the change, and the files released by the change deleted once
/// committed (best effort: an orphan blob costs a few kilobytes, a failed request would be worse).
/// </summary>
internal sealed partial class HandoverStore(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IFileStorage storage,
    IUserContext user,
    TimeProvider clock,
    ILogger<HandoverStore> logger)
{
    public IAppDbContext Db => db;

    public IUserContext User => user;

    public IFileStorage Storage => storage;

    public DateTimeOffset Now() => clock.GetUtcNow();

    public BookingActor Actor() => BookingMapping.ActorFor(user);

    public Task<Inspection?> FindAsync(Guid inspectionId, bool tracked, CancellationToken cancellationToken) =>
        Query(tracked).FirstOrDefaultAsync(i => i.Id == inspectionId, cancellationToken);

    /// <summary>The latest inspection of <paramref name="kind"/> of a file (draft or completed), or <see langword="null"/>.</summary>
    public Task<Inspection?> FindForBookingAsync(Guid bookingId, InspectionKind kind, bool tracked, CancellationToken cancellationToken) =>
        Query(tracked)
            .Where(i => i.BookingId == bookingId && i.Kind == kind)
            .OrderByDescending(i => i.StartedAtUtc)
            .ThenByDescending(i => i.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The last completed inspection of a vehicle (its damages are the pre-existing ones of the next departure).</summary>
    public Task<Inspection?> LastCompletedOfVehicleAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        Query(tracked: false)
            .Where(i => i.VehicleId == vehicleId && i.Status == InspectionStatus.Completed)
            .OrderByDescending(i => i.CompletedAtUtc)
            .ThenByDescending(i => i.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<Booking?> FindBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
        db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

    /// <summary>
    /// Changes a draft inspection in a unit of work: hand-over permission, inspection and file loaded inside (a departure
    /// changes while the file is confirmed, a return while the rental is in progress), then the released files deleted.
    /// </summary>
    public async Task<Result<T>> ChangeAsync<T>(
        Guid inspectionId,
        Func<Inspection, Booking, CancellationToken, Task<Result<T>>> change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!user.HasPermission(Permissions.HandoversManage))
        {
            return BookingErrors.Forbidden;
        }

        IReadOnlyList<string> released = [];
        Result<T> result;
        try
        {
            result = await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    released = [];
                    var inspection = await FindAsync(inspectionId, tracked: true, ct);
                    if (inspection is null)
                    {
                        return Result.Failure<T>(HandoverErrors.InspectionNotFound);
                    }

                    var booking = await FindBookingAsync(inspection.BookingId, ct);
                    if (booking is null)
                    {
                        return Result.Failure<T>(BookingErrors.NotFound);
                    }

                    var open = EnsureOpen(inspection, booking);
                    if (open.IsFailure)
                    {
                        return Result.Failure<T>(open.Error);
                    }

                    var outcome = await change(inspection, booking, ct);
                    released = inspection.DequeueReleasedFiles();
                    return outcome;
                },
                cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return HandoverErrors.ConcurrentUpdate;
        }

        if (result.IsSuccess)
        {
            await DeleteAsync(FileContainers.Inspections, released);
        }

        return result;
    }

    /// <inheritdoc cref="ChangeAsync{T}"/>
    public async Task<Result> ChangeAsync(Guid inspectionId, Func<Inspection, Booking, Result> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var result = await ChangeAsync(
            inspectionId,
            (inspection, booking, _) =>
            {
                var outcome = change(inspection, booking);
                return Task.FromResult(outcome.IsSuccess ? Result.Success(true) : Result.Failure<bool>(outcome.Error));
            },
            cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    /// <summary>
    /// Runs <paramref name="operation"/> in a unit of work (replayable: it loads what it needs inside); a concurrent change
    /// of an inspection becomes <see cref="HandoverErrors.ConcurrentUpdate"/>, a concurrent creation of a draft (unique
    /// index) is retried once by the caller.
    /// </summary>
    public async Task<Result<T>> RunAsync<T>(Func<CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await unitOfWork.ExecuteAsync(operation, cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return HandoverErrors.ConcurrentUpdate;
        }
    }

    /// <summary>A departure changes while its file is confirmed, a return while the rental is in progress.</summary>
    public static Result EnsureOpen(Inspection inspection, Booking booking)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(booking);
        var expected = inspection.Kind == InspectionKind.Departure ? BookingStatus.Confirmed : BookingStatus.InProgress;
        return !inspection.IsDraft || booking.Status == expected ? Result.Success() : BookingErrors.InvalidTransition;
    }

    /// <summary>Stores a file (before the database change that references it).</summary>
    public async Task StoreAsync(string container, string path, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(content, writable: false);
        await storage.UploadAsync(container, path, stream, contentType, cancellationToken);
    }

    /// <summary>Deletes files no longer referenced (best effort, never fails the request).</summary>
    public async Task DeleteAsync(string container, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            try
            {
                await storage.DeleteAsync(container, path, CancellationToken.None);
            }
#pragma warning disable CA1031 // Cleanup of a stored file must never fail the request.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogDeleteFailed(exception, container, path);
            }
        }
    }

    /// <summary>Reads a stored file fully (signatures of a contract), or <see langword="null"/> when it is missing.</summary>
    public async Task<byte[]?> ReadAsync(string container, string path, CancellationToken cancellationToken)
    {
        await using var download = await storage.OpenReadAsync(container, path, cancellationToken);
        if (download is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await download.Content.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private IQueryable<Inspection> Query(bool tracked) => tracked ? db.Set<Inspection>() : db.Set<Inspection>().AsNoTracking();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hand-over file {Container}/{Path} could not be deleted from storage.")]
    private partial void LogDeleteFailed(Exception exception, string container, string path);
}
