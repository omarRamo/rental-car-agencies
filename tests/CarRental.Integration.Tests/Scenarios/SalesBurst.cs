using CarRental.Domain.Common;

namespace CarRental.Integration.Tests.Scenarios;

/// <summary>Where a sale comes from in the rush (the channels and their entry points).</summary>
internal enum SaleSource
{
    /// <summary>A web request through the public contract (<c>IBookingRequests</c>).</summary>
    Web,

    /// <summary>Step 3 of the booking funnel (<c>FunnelService</c>), the session opened before the rush.</summary>
    Funnel,

    /// <summary>A phone option (<c>QuickBookingService</c>).</summary>
    Phone,

    /// <summary>A walk-in confirmed at the counter (<c>QuickBookingService</c>).</summary>
    Counter,

    /// <summary>A Booking.com reservation of the extranet entered by hand: restrictions ignored, never the capacity.</summary>
    BookingComExtranet,

    /// <summary>A reservation of a connected Booking.com (<c>IChannelBookings</c>): its quotas and stop-sells apply.</summary>
    BookingComConnected,
}

/// <summary>One attempt to sell a vehicle of <paramref name="Category"/>; <paramref name="Sell"/> returns the booking created.</summary>
internal sealed record SaleAttempt(SaleSource Source, SeasonCategory Category, Func<Task<Result<Guid>>> Sell);

/// <summary>What became of an attempt: the booking sold, a refusal, or an exception (a bug: never expected).</summary>
internal sealed record SaleOutcome(SaleAttempt Attempt, Result<Guid>? Result, Exception? Exception)
{
    public bool IsSold => Result is { IsSuccess: true };

    public string? RefusalCode => Result is { IsFailure: true } refused ? refused.Error.Code : null;

    public override string ToString() =>
        $"{Attempt.Source} {Attempt.Category.Code}: {(Exception is not null ? Exception.GetType().Name + " " + Exception.Message : IsSold ? "sold" : RefusalCode)}";
}

/// <summary>
/// The evening rush: every attempt waits for the same signal, then all run at once, each in a scope of its own (a
/// request, a tablet, a channel message), so that they really race for the lock of their category.
/// </summary>
internal static class SalesBurst
{
    public static async Task<IReadOnlyList<SaleOutcome>> RunAsync(IReadOnlyList<SaleAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = attempts.Select(async attempt =>
        {
            await go.Task;
            try
            {
                return new SaleOutcome(attempt, await attempt.Sell(), null);
            }
#pragma warning disable CA1031 // An exception is an outcome to report with the others, not a reason to lose them.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                return new SaleOutcome(attempt, null, exception);
            }
        }).ToList();
        go.SetResult();
        return await Task.WhenAll(running);
    }
}
