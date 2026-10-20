using CarRental.Application.Bookings.Writing;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Domain.Bookings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Bookings.Jobs;

/// <summary>
/// Every minute: expires the web requests whose hold is over (<c>PendingValidation</c>, <c>InformationRequested</c>), the
/// options past their expiry (<c>OptionHeld</c>) and the validated requests past their payment deadline
/// (<c>AwaitingPayment</c>) — PROMPT §6 invariant 11. Each file in its own unit of work, as the system actor, with its
/// timeline row; the availability cache is told once committed. Idempotent: a file already handled is skipped.
/// </summary>
internal sealed partial class BookingExpiryJob(BookingWriter writer, ILogger<BookingExpiryJob> logger) : IRecurringJob
{
    public const int BatchSize = 200;

    public static string JobId => "bookings.expire";

    public static string CronExpression => "* * * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = writer.Now();
        var due = await writer.Db.Set<Booking>()
            .AsNoTracking()
            .Where(b => ((b.Status == BookingStatus.PendingValidation || b.Status == BookingStatus.InformationRequested) && b.HoldExpiresAt <= now)
                        || (b.Status == BookingStatus.OptionHeld && b.OptionExpiresAt <= now)
                        || (b.Status == BookingStatus.AwaitingPayment && b.PaymentDueAt <= now))
            .OrderBy(b => b.Id)
            .Select(b => new { b.Id, b.Number })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var booking in due)
        {
            var outcome = await writer.TransitionAsync(
                booking.Id,
                (tracked, at, _) => Task.FromResult(tracked.Expire(writer.Actor(), at)),
                cancellationToken);
            if (outcome.IsSuccess)
            {
                expired++;
            }
            else
            {
                // Validated, cancelled or expired meanwhile (another run, an agent): nothing to do.
                LogSkipped(booking.Number, outcome.Error.Code);
            }
        }

        if (expired > 0)
        {
            LogExpired(expired);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} rental file(s) expired (hold, option or payment deadline passed).")]
    private partial void LogExpired(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rental file {Number} not expired: {Code}.")]
    private partial void LogSkipped(string number, string code);
}
