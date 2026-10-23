using CarRental.Application.Bookings.Availability;
using CarRental.Application.Common.Availability;
using CarRental.Application.Common.Numbering;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Domain.Bookings;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings.Writing;

/// <summary>
/// The write pipeline of the rental files (ADR-0032): one unit of work per command, replayable after a transient
/// failure (the operation loads what it needs inside), the availability guard for the commands that consume capacity
/// (it takes the lock of the category itself), the readable number in the same transaction, the status message in the
/// outbox, and — only once committed — the availability cache told about every category touched.
/// </summary>
internal sealed class BookingWriter(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IAvailabilityGuard guard,
    IAvailabilityChangeNotifier notifier,
    IDocumentNumberGenerator numbers,
    IOutbox outbox,
    IUserContext user,
    TimeProvider clock)
{
    public IAppDbContext Db => db;

    public IAvailabilityGuard Guard => guard;

    public IUserContext User => user;

    public DateTimeOffset Now() => clock.GetUtcNow();

    /// <summary>The actor of the operation (a user, or the system actor of a job).</summary>
    public BookingActor Actor(SalesChannel? channel = null) => BookingMapping.ActorFor(user, channel);

    /// <summary>
    /// Whether the operation may write the files of <paramref name="agencyId"/> (ADR-0067): an operation across agencies
    /// writes for any, an operation of one agency for its own only.
    /// </summary>
    public bool WorksFor(Guid agencyId) => user.IsAcrossAgencies || (agencyId != Guid.Empty && user.AgencyId == agencyId);

    /// <summary>
    /// Runs <paramref name="operation"/> in a unit of work. The categories it adds to the set are told to the availability
    /// cache after the commit, never before nor after a rollback; a concurrent change of the same file becomes
    /// <see cref="BookingErrors.ConcurrentUpdate"/>.
    /// </summary>
    public async Task<Result<T>> RunAsync<T>(
        Func<ISet<Guid>, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var touched = new HashSet<Guid>();
        Result<T> result;
        try
        {
            result = await unitOfWork.ExecuteAsync(
                ct =>
                {
                    // A replay after a transient failure starts from scratch.
                    touched.Clear();
                    return operation(touched, ct);
                },
                cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return BookingErrors.ConcurrentUpdate;
        }

        if (result.IsSuccess)
        {
            foreach (var categoryId in touched)
            {
                notifier.CategoryChanged(categoryId);
            }
        }

        return result;
    }

    /// <inheritdoc cref="RunAsync{T}(Func{ISet{Guid}, CancellationToken, Task{Result{T}}}, CancellationToken)"/>
    public async Task<Result> RunAsync(Func<ISet<Guid>, CancellationToken, Task<Result>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var result = await RunAsync<bool>(
            async (touched, ct) =>
            {
                var outcome = await operation(touched, ct);
                return outcome.IsSuccess ? Result.Success(true) : Result.Failure<bool>(outcome.Error);
            },
            cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    /// <summary>Loads a rental file for a command (tracked; its stored timeline is not loaded).</summary>
    public Task<Booking?> LoadAsync(Guid bookingId, CancellationToken cancellationToken) =>
        db.Set<Booking>().FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

    /// <summary>
    /// The next <c>LOC</c> number of <paramref name="agencyId"/> for the Tunis year of <paramref name="at"/>, inside the
    /// transaction (ADR-0016, one series per agency).
    /// </summary>
    public Task<DocumentNumber> NextNumberAsync(Guid agencyId, DateTimeOffset at, CancellationToken cancellationToken) =>
        numbers.NextForDateAsync(agencyId, DocumentType.Booking, at, cancellationToken);

    /// <summary>Publishes the status change of a file to the outbox (same transaction).</summary>
    public void PublishStatusChange(Booking booking, BookingStatus? previousStatus, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(booking);
        outbox.Enqueue(booking.StatusChanged(previousStatus, at));
    }

    /// <summary>
    /// Applies a status transition loaded and checked inside the unit of work: loads the file, runs
    /// <paramref name="command"/>, publishes the status change and touches its category.
    /// </summary>
    public Task<Result> TransitionAsync(
        Guid bookingId,
        Func<Booking, DateTimeOffset, CancellationToken, Task<Result>> command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RunAsync(
            async (touched, ct) =>
            {
                var booking = await LoadAsync(bookingId, ct);
                if (booking is null)
                {
                    return BookingErrors.NotFound;
                }

                var before = booking.Status;
                var now = Now();
                var outcome = await command(booking, now, ct);
                if (outcome.IsFailure)
                {
                    return outcome;
                }

                if (booking.Status != before)
                {
                    PublishStatusChange(booking, before, now);
                }

                touched.Add(booking.CategoryId);
                return Result.Success();
            },
            cancellationToken);
    }
}
