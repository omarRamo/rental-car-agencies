using CarRental.Application.Bookings.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Reporting;

/// <summary>Facts of rental files for the tests of the indicators (statuses and timelines built step by step).</summary>
internal static class ReportingTestData
{
    public static readonly Guid Agency = Guid.CreateVersion7();
    public static readonly Guid Ecmr = Guid.CreateVersion7();
    public static readonly Guid Cdmr = Guid.CreateVersion7();

    public static DateTimeOffset At(DateOnly day, int hour, int minute = 0) => TunisTime.FromLocal(day, new TimeOnly(hour, minute));

    /// <summary>
    /// A file of <paramref name="days"/> days from <paramref name="pickupAt"/>, whose timeline goes through
    /// <paramref name="steps"/> (status reached and when), its revenue HT <paramref name="revenue"/> plus the daily
    /// contribution (2.000 HT per day).
    /// </summary>
    public static BookingFactsInfo Booking(
        DateTimeOffset pickupAt,
        int days,
        decimal revenue = 100m,
        SalesChannel channel = SalesChannel.Phone,
        Guid? category = null,
        string? source = null,
        Guid? vehicle = null,
        decimal? cancellationFee = null,
        DateTimeOffset? createdAt = null,
        params (BookingStatusInfo Status, DateTimeOffset At)[] steps)
    {
        var created = createdAt ?? pickupAt.AddDays(-10);
        var history = new List<BookingStatusChangeInfo>();
        BookingStatusInfo? before = null;
        foreach (var (reached, at) in steps.Length == 0 ? [(BookingStatusInfo.Confirmed, created)] : steps)
        {
            history.Add(new BookingStatusChangeInfo(at, before, reached));
            before = reached;
        }

        var contribution = Money.Tnd(2m * days);
        var status = history[^1].To;
        var submitted = history.FirstOrDefault(h => h.To == BookingStatusInfo.PendingValidation)?.AtUtc;
        return new BookingFactsInfo(
            Guid.CreateVersion7(),
            $"LOC-2026-{Random.Shared.Next(1, 999_999):D6}",
            Agency,
            status,
            channel,
            source,
            category ?? Ecmr,
            pickupAt,
            pickupAt.AddDays(days),
            status is BookingStatusInfo.InProgress or BookingStatusInfo.Returned or BookingStatusInfo.Closed ? pickupAt : null,
            status is BookingStatusInfo.Returned or BookingStatusInfo.Closed ? pickupAt.AddDays(days) : null,
            created,
            submitted,
            new BookingQuoteFactsInfo(Guid.CreateVersion7(), 1, days, Money.Tnd(revenue) + contribution, Money.Zero, Money.Tnd(revenue) + contribution, contribution),
            status == BookingStatusInfo.Cancelled ? Money.Tnd(cancellationFee ?? 0m) : null,
            vehicle is { } assigned ? [new BookingVehicleSpanInfo(assigned, null)] : [],
            history);
    }
}
