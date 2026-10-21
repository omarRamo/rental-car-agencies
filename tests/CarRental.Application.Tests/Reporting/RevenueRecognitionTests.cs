using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Reporting;
using CarRental.Domain.Common;
using static CarRental.Application.Tests.Reporting.ReportingTestData;

namespace CarRental.Application.Tests.Reporting;

/// <summary>What counts as rental revenue (ADR-0045).</summary>
public sealed class RevenueRecognitionTests
{
    private static readonly DateOnly Day = new(2026, 9, 10);

    [Theory]
    [InlineData(BookingStatusInfo.Confirmed, true, false)]
    [InlineData(BookingStatusInfo.InProgress, true, false)]
    [InlineData(BookingStatusInfo.Returned, true, false)]
    [InlineData(BookingStatusInfo.Closed, true, false)]
    [InlineData(BookingStatusInfo.Draft, false, false)]
    [InlineData(BookingStatusInfo.PendingValidation, false, true)]
    [InlineData(BookingStatusInfo.InformationRequested, false, true)]
    [InlineData(BookingStatusInfo.OptionHeld, false, true)]
    [InlineData(BookingStatusInfo.AwaitingPayment, false, true)]
    [InlineData(BookingStatusInfo.Refused, false, false)]
    [InlineData(BookingStatusInfo.Expired, false, false)]
    [InlineData(BookingStatusInfo.Cancelled, false, false)]
    [InlineData(BookingStatusInfo.NoShow, false, false)]
    public void Only_firm_files_count_as_revenue_and_open_ones_are_tentative(BookingStatusInfo status, bool counts, bool tentative)
    {
        RevenueRecognition.Counts(status).ShouldBe(counts);
        RevenueRecognition.IsTentative(status).ShouldBe(tentative);
    }

    [Fact]
    public void The_rental_revenue_is_the_quote_excluding_vat_minus_the_daily_contribution()
    {
        // The reference quote of SPEC §7: 572.605 HT of which 10.000 HT of daily contribution.
        var quote = new BookingQuoteFactsInfo(Guid.CreateVersion7(), 1, 5, Money.Tnd(572.605m), Money.Tnd(108.795m), Money.Tnd(681.4m), Money.Tnd(10m));

        RevenueRecognition.RentalRevenue(quote).ShouldBe(Money.Tnd(562.605m));
    }

    [Fact]
    public void A_file_was_confirmed_when_its_timeline_went_through_confirmed_or_beyond()
    {
        var pickup = At(Day, 10);
        var cancelledAfterConfirmation = Booking(pickup, 3, steps: [(BookingStatusInfo.Confirmed, pickup.AddDays(-5)), (BookingStatusInfo.Cancelled, pickup.AddDays(-2))]);
        var cancelledRequest = Booking(pickup, 3, steps: [(BookingStatusInfo.Draft, pickup.AddDays(-5)), (BookingStatusInfo.PendingValidation, pickup.AddDays(-5)), (BookingStatusInfo.Cancelled, pickup.AddDays(-4))]);
        var noShow = Booking(pickup, 3, steps: [(BookingStatusInfo.NoShow, pickup.AddHours(3))]);

        RevenueRecognition.WasConfirmed(cancelledAfterConfirmation).ShouldBeTrue();
        RevenueRecognition.WasConfirmed(cancelledRequest).ShouldBeFalse();
        RevenueRecognition.WasConfirmed(noShow).ShouldBeTrue();
    }

    [Fact]
    public void The_status_at_an_instant_comes_from_the_timeline()
    {
        var pickup = At(Day, 10);
        var booking = Booking(pickup, 3, steps:
        [
            (BookingStatusInfo.OptionHeld, pickup.AddDays(-6)),
            (BookingStatusInfo.Confirmed, pickup.AddDays(-4)),
            (BookingStatusInfo.InProgress, pickup),
        ]);

        RevenueRecognition.StatusAt(booking, pickup.AddDays(-7)).ShouldBeNull();
        RevenueRecognition.StatusAt(booking, pickup.AddDays(-5)).ShouldBe(BookingStatusInfo.OptionHeld);
        RevenueRecognition.StatusAt(booking, pickup.AddDays(-4)).ShouldBe(BookingStatusInfo.Confirmed);
        RevenueRecognition.StatusAt(booking, pickup.AddDays(1)).ShouldBe(BookingStatusInfo.InProgress);
    }
}
