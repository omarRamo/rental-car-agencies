using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Reporting.Indicators;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using static CarRental.Application.Tests.Reporting.ReportingTestData;

namespace CarRental.Application.Tests.Reporting;

/// <summary>Conversion of the website and of the bot: searches → requests → confirmed, by channel and source (ADR-0046).</summary>
public sealed class FunnelCalculatorTests
{
    private static readonly DateOnly Day = new(2026, 10, 5);

    [Fact]
    public void Searches_requests_and_confirmations_meet_by_channel_and_normalized_source()
    {
        FunnelCount[] counts =
        [
            new(SalesChannel.Web, null, FunnelStep.Search, 100),
            new(SalesChannel.Web, null, FunnelStep.Results, 80),
            new(SalesChannel.Web, "google", FunnelStep.Search, 50),
            new(SalesChannel.Web, "Google ", FunnelStep.Results, 40),
            new(SalesChannel.WhatsApp, "whatsapp", FunnelStep.Search, 20),
            new(SalesChannel.WhatsApp, "whatsapp", FunnelStep.Results, 18),
        ];
        BookingFactsInfo[] requests =
        [
            .. Requests(SalesChannel.Web, null, submitted: 5, confirmed: 3),
            .. Requests(SalesChannel.Web, "GOOGLE", submitted: 2, confirmed: 1),
            .. Requests(SalesChannel.WhatsApp, "whatsapp", submitted: 4, confirmed: 2),
            .. Requests(SalesChannel.Phone, null, submitted: 3, confirmed: 3),
        ];

        var funnel = FunnelCalculator.Compute(counts, requests);

        funnel.Rows.Select(r => (r.Channel, r.Source, r.Searches, r.Results, r.Requests, r.Confirmed)).ShouldBe(
        [
            (SalesChannel.Web, null, 100, 80, 5, 3),
            (SalesChannel.Web, "google", 50, 40, 2, 1),
            (SalesChannel.WhatsApp, "whatsapp", 20, 18, 4, 2),
        ]);
        funnel.Total.ShouldBe(new FunnelRow(null, null, 170, 138, 11, 6));
        var direct = funnel.Rows[0];
        direct.SearchToRequest.ShouldBe(0.05m);
        direct.RequestToConfirmed.ShouldBe(0.6m);
        direct.SearchToConfirmed.ShouldBe(0.03m);
    }

    [Fact]
    public void Requests_without_searches_and_searches_without_requests_both_show_with_undefined_rates()
    {
        FunnelCount[] counts = [new(SalesChannel.Web, "facebook", FunnelStep.Search, 12)];
        BookingFactsInfo[] requests = [.. Requests(SalesChannel.Web, "newsletter", submitted: 1, confirmed: 0)];

        var funnel = FunnelCalculator.Compute(counts, requests);

        var newsletter = funnel.Rows.Single(r => r.Source == "newsletter");
        newsletter.SearchToRequest.ShouldBeNull();
        newsletter.RequestToConfirmed.ShouldBe(0m);
        var facebook = funnel.Rows.Single(r => r.Source == "facebook");
        facebook.RequestToConfirmed.ShouldBeNull();
        facebook.SearchToRequest.ShouldBe(0m);
        FunnelCalculator.Compute([], []).Total.SearchToRequest.ShouldBeNull();
    }

    private static IEnumerable<BookingFactsInfo> Requests(SalesChannel channel, string? source, int submitted, int confirmed)
    {
        var submittedAt = At(Day, 9);
        for (var i = 0; i < submitted; i++)
        {
            var outcome = i < confirmed ? BookingStatusInfo.Confirmed : BookingStatusInfo.Refused;
            yield return Booking(
                At(Day.AddDays(10), 10),
                3,
                channel: channel,
                source: source,
                steps: [(BookingStatusInfo.Draft, submittedAt), (BookingStatusInfo.PendingValidation, submittedAt), (outcome, submittedAt.AddHours(1))]);
        }
    }
}
