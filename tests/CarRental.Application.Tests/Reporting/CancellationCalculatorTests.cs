using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Reporting.Indicators;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using static CarRental.Application.Tests.Reporting.ReportingTestData;

namespace CarRental.Application.Tests.Reporting;

/// <summary>Cancellations and no-shows by channel among the confirmed files due to start in the period (ADR-0046).</summary>
public sealed class CancellationCalculatorTests
{
    private static readonly ReportPeriod October = ReportPeriod.Month(2026, 10);

    [Fact]
    public void Rates_are_taken_among_the_confirmed_files_due_to_start_in_the_period()
    {
        var day = October.From;
        BookingFactsInfo[] bookings =
        [
            Booking(At(day.AddDays(4), 10), 3, channel: SalesChannel.Web, cancellationFee: 30m, steps: [(BookingStatusInfo.Confirmed, At(day, 9)), (BookingStatusInfo.Cancelled, At(day.AddDays(2), 9))]),
            Booking(At(day.AddDays(9), 10), 3, channel: SalesChannel.Web, steps: [(BookingStatusInfo.Confirmed, At(day, 9)), (BookingStatusInfo.InProgress, At(day.AddDays(9), 10))]),
            Booking(At(day.AddDays(11), 10), 3, channel: SalesChannel.Web, steps: [(BookingStatusInfo.PendingValidation, At(day, 9)), (BookingStatusInfo.Cancelled, At(day, 10))]),
            Booking(At(new DateOnly(2026, 11, 2), 10), 3, channel: SalesChannel.Web, steps: [(BookingStatusInfo.Confirmed, At(day, 9)), (BookingStatusInfo.Cancelled, At(day, 10))]),
            Booking(At(day.AddDays(14), 10), 3, channel: SalesChannel.Phone, steps: [(BookingStatusInfo.OptionHeld, At(day, 9)), (BookingStatusInfo.Confirmed, At(day, 12)), (BookingStatusInfo.NoShow, At(day.AddDays(14), 13))]),
            Booking(At(day.AddDays(19), 10), 3, channel: SalesChannel.Phone, steps: [(BookingStatusInfo.Confirmed, At(day, 9)), (BookingStatusInfo.Closed, At(day.AddDays(23), 9))]),
        ];

        var report = CancellationCalculator.Compute(bookings, October);

        var total = report.Total;
        (total.FirmBookings, total.Cancelled, total.NoShows, total.CancelledBeforeConfirmation).ShouldBe((4, 1, 1, 1));
        total.Fees.ShouldBe(Money.Tnd(30m));
        total.CancellationRate.ShouldBe(0.25m);
        total.NoShowRate.ShouldBe(0.25m);
        var web = report.Channels.Single(c => c.Channel == SalesChannel.Web);
        (web.FirmBookings, web.Cancelled, web.CancelledBeforeConfirmation, web.CancellationRate).ShouldBe((2, 1, 1, (decimal?)0.5m));
        var phone = report.Channels.Single(c => c.Channel == SalesChannel.Phone);
        (phone.FirmBookings, phone.NoShows, phone.NoShowRate).ShouldBe((2, 1, (decimal?)0.5m));
        report.Channels.Single(c => c.Channel == SalesChannel.BookingCom).CancellationRate.ShouldBeNull();
    }
}
