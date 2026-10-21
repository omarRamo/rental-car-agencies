using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Reporting.Indicators;
using CarRental.Domain.Common;
using static CarRental.Application.Tests.Reporting.ReportingTestData;

namespace CarRental.Application.Tests.Reporting;

/// <summary>Validation delay (median, opening hours) and refusal rate of the web requests, from their timeline (ADR-0046).</summary>
public sealed class RequestQualityCalculatorTests
{
    private static readonly DateOnly Monday = new(2026, 10, 19);
    private static readonly TimeOnly Opens = new(8, 0);
    private static readonly TimeOnly Closes = new(20, 0);

    [Fact]
    public void The_delay_is_the_time_waiting_for_an_agent_and_the_time_waiting_for_the_customer_is_left_out()
    {
        var request = Request(
            SalesChannel.Web,
            (BookingStatusInfo.PendingValidation, At(Monday, 19)),
            (BookingStatusInfo.InformationRequested, At(Monday, 19, 30)),
            (BookingStatusInfo.PendingValidation, At(Monday.AddDays(1), 9)),
            (BookingStatusInfo.Refused, At(Monday.AddDays(1), 9, 45)));

        var outcome = RequestQualityCalculator.Handle(request, Opens, Closes);

        outcome.Kind.ShouldBe(RequestOutcomeKind.Refused);
        outcome.Delay.ShouldBe(TimeSpan.FromMinutes(75));
        outcome.BusinessDelay.ShouldBe(TimeSpan.FromMinutes(75));
    }

    [Fact]
    public void A_night_counts_in_clock_time_but_not_in_opening_hours()
    {
        var request = Request(
            SalesChannel.WhatsApp,
            (BookingStatusInfo.PendingValidation, At(Monday, 21)),
            (BookingStatusInfo.AwaitingPayment, At(Monday.AddDays(1), 11)));

        var outcome = RequestQualityCalculator.Handle(request, Opens, Closes);

        outcome.Kind.ShouldBe(RequestOutcomeKind.Validated);
        outcome.Delay.ShouldBe(TimeSpan.FromHours(14));
        outcome.BusinessDelay.ShouldBe(TimeSpan.FromHours(3));
    }

    [Fact]
    public void Medians_rates_and_the_target_are_computed_on_the_decided_requests()
    {
        BookingFactsInfo[] requests =
        [
            Request(SalesChannel.Web, (BookingStatusInfo.PendingValidation, At(Monday, 9)), (BookingStatusInfo.Confirmed, At(Monday, 10, 30))),
            Request(
                SalesChannel.Web,
                (BookingStatusInfo.PendingValidation, At(Monday, 19)),
                (BookingStatusInfo.InformationRequested, At(Monday, 19, 30)),
                (BookingStatusInfo.PendingValidation, At(Monday.AddDays(1), 9)),
                (BookingStatusInfo.Refused, At(Monday.AddDays(1), 9, 45))),
            Request(SalesChannel.WhatsApp, (BookingStatusInfo.PendingValidation, At(Monday, 21)), (BookingStatusInfo.AwaitingPayment, At(Monday.AddDays(1), 11))),
            Request(SalesChannel.Web, (BookingStatusInfo.PendingValidation, At(Monday, 9)), (BookingStatusInfo.Expired, At(Monday.AddDays(1), 9))),
            Request(SalesChannel.Web, (BookingStatusInfo.PendingValidation, At(Monday, 9)), (BookingStatusInfo.Cancelled, At(Monday, 11))),
            Request(SalesChannel.Web, (BookingStatusInfo.PendingValidation, At(Monday, 9))),
        ];

        var report = RequestQualityCalculator.Compute(requests, Opens, Closes);

        var total = report.Total;
        (total.Submitted, total.Validated, total.Refused, total.Expired, total.Cancelled, total.Pending).ShouldBe((6, 2, 1, 1, 1, 1));
        total.Decided.ShouldBe(3);
        total.MedianDelay.ShouldBe(TimeSpan.FromMinutes(90));
        total.MedianBusinessDelay.ShouldBe(TimeSpan.FromMinutes(90));
        total.RefusalRate.ShouldBe(1m / 3m);
        total.ExpiryRate.ShouldBe(1m / 6m);
        total.DecidedWithinTarget.ShouldBe(2);
        total.WithinTargetRate.ShouldBe(2m / 3m);
        report.Target.ShouldBe(TimeSpan.FromHours(2));

        var web = report.Channels.Single(c => c.Channel == SalesChannel.Web);
        (web.Submitted, web.Decided).ShouldBe((5, 2));
        web.MedianDelay.ShouldBe(TimeSpan.FromMinutes(82.5));
        report.Channels.Single(c => c.Channel == SalesChannel.WhatsApp).RefusalRate.ShouldBe(0m);
    }

    [Fact]
    public void Without_decided_requests_there_is_no_median_and_no_rate()
    {
        var report = RequestQualityCalculator.Compute([Request(SalesChannel.Web, (BookingStatusInfo.PendingValidation, At(Monday, 9)))], Opens, Closes);

        report.Total.MedianDelay.ShouldBeNull();
        report.Total.RefusalRate.ShouldBeNull();
        report.Total.WithinTargetRate.ShouldBeNull();
        RequestQualityCalculator.Compute([], Opens, Closes).Total.ExpiryRate.ShouldBeNull();
    }

    private static BookingFactsInfo Request(SalesChannel channel, params (BookingStatusInfo Status, DateTimeOffset At)[] steps) =>
        Booking(At(Monday.AddDays(20), 10), 3, channel: channel, steps: [(BookingStatusInfo.Draft, steps[0].At.AddMinutes(-5)), .. steps]);
}
