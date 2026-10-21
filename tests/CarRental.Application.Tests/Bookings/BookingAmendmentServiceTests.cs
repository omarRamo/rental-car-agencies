using CarRental.Application.Bookings;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Tests.Bookings.Fakes;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using QuoteLineKind = CarRental.Domain.Bookings.Quotes.QuoteLineKind;

namespace CarRental.Application.Tests.Bookings;

/// <summary>Amendments (priced again), extensions (added days at the current plan) and manual discounts: new quote versions.</summary>
public sealed class BookingAmendmentServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AmendBookingRequest Current(Booking booking) => new(
        booking.Id,
        booking.CategoryId,
        booking.PickupLocationId,
        booking.ReturnLocationId,
        booking.PickupAt,
        booking.ReturnAt,
        [.. booking.Extras.Select(e => new SelectedExtra(e.Code, e.Quantity))],
        booking.PromoCode,
        booking.GuaranteedVehicleId,
        booking.MainDriverId!.Value,
        booking.SecondaryDriverId,
        booking.PayerCustomerId,
        booking.Notes);

    [Fact]
    public async Task New_dates_are_priced_again_at_the_current_plan_as_a_new_version_checked_under_the_lock()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();
        harness.Pricing.DailyPrices[harness.Ecmr.Id] = 120m;

        var amended = await harness.Amendments.AmendAsync(Current(booking) with { ReturnAt = booking.ReturnAt.AddDays(1) }, Ct);

        amended.IsSuccess.ShouldBeTrue(amended.IsFailure ? amended.Error.ToString() : null);
        amended.Value.QuoteVersion.ShouldBe(2);
        amended.Value.TotalInclVat.ShouldBe(Money.Tnd(489.520m), "4 days at the current 120.000 and the contribution");
        var versions = harness.Db.Rows<Quote>().Where(q => q.BookingId == booking.Id).OrderBy(q => q.Version).ToList();
        versions.Select(q => q.Version).ShouldBe([1, 2]);
        versions[0].TotalInclVat.ShouldBe(Money.Tnd(307.140m), "the previous version is kept unchanged");
        versions[1].Origin.ShouldBe(QuoteOrigin.Requote);
        var (request, previous) = harness.Guard.AvailabilityChecks.ShouldHaveSingleItem();
        request.Booking.Rental.End.ShouldBe(booking.ReturnAt);
        request.Booking.BookingId.ShouldBe(booking.Id);
        previous.ShouldBeNull();
        harness.Notifier.Changed.ShouldBe([harness.Ecmr.Id]);
        booking.Events[^1].Type.ShouldBe(BookingEventType.Amended);
    }

    [Fact]
    public async Task A_category_change_locks_both_categories_and_releases_the_vehicle()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();
        (await harness.Assignments.AssignAsync(booking.Id, harness.VehicleA.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        harness.Reset();

        (await harness.Amendments.AmendAsync(Current(booking) with { CategoryId = harness.Cdmr.Id }, Ct)).IsSuccess.ShouldBeTrue();

        var (request, previous) = harness.Guard.AvailabilityChecks.ShouldHaveSingleItem();
        request.CategoryId.ShouldBe(harness.Cdmr.Id);
        request.Booking.AssignedVehicleId.ShouldBeNull("the vehicle belongs to the former category");
        previous.ShouldBe(harness.Ecmr.Id);
        booking.AssignedVehicleId.ShouldBeNull();
        harness.Notifier.Changed.ShouldBe([harness.Ecmr.Id, harness.Cdmr.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task The_payer_changes_without_a_new_price_nor_a_check()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();
        var otherPayer = (await harness.Customers.GetOrCreateIndividualCustomerAsync(harness.SecondDriverId, Ct)).Value;

        var amended = await harness.Amendments.AmendAsync(Current(booking) with { PayerCustomerId = otherPayer }, Ct);

        amended.Value.QuoteVersion.ShouldBe(1);
        booking.PayerCustomerId.ShouldBe(otherPayer);
        harness.Guard.AvailabilityChecks.ShouldBeEmpty();
        harness.Pricing.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_extension_keeps_the_frozen_price_and_adds_the_days_at_the_current_plan()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();
        (await harness.Assignments.AssignAsync(booking.Id, harness.VehicleA.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        harness.Clock.SetUtcNow(harness.PickupAt);
        harness.As(Roles.Preparer);
        (await harness.Lifecycle.StartRentalAsync(booking.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        harness.As(Roles.Agent);
        harness.Reset();
        harness.Pricing.DailyPrices[harness.Ecmr.Id] = 130m;
        harness.Clock.SetUtcNow(harness.PickupAt.AddDays(2));

        var extended = await harness.Amendments.AmendAsync(Current(booking) with { ReturnAt = booking.ReturnAt.AddDays(2) }, Ct);

        extended.IsSuccess.ShouldBeTrue(extended.IsFailure ? extended.Error.ToString() : null);
        var quote = harness.Db.Rows<Quote>().Single(q => q.BookingId == booking.Id && q.Version == 2);
        quote.Origin.ShouldBe(QuoteOrigin.Extension);
        quote.Lines.Select(l => l.Kind).ShouldBe([QuoteLineKind.Rental, QuoteLineKind.DailyContribution, QuoteLineKind.Extension, QuoteLineKind.DailyContribution]);
        quote.Lines[0].InclVat.ShouldBe(Money.Tnd(300m), "the three booked days keep their frozen price");
        quote.Lines[2].InclVat.ShouldBe(Money.Tnd(260m), "two added days at the current 130.000");
        quote.Lines[3].LabelKey.ShouldBe(BookingQuoteLabels.ExtensionContribution);
        quote.TotalInclVat.ShouldBe(Money.Tnd(307.140m + 260m + 4.760m));
        harness.Pricing.Requests.Select(r => r.ReturnAt).ShouldBe([booking.ReturnAt, booking.ReturnAt.AddDays(-2)], ignoreOrder: true);
        var (request, _) = harness.Guard.AvailabilityChecks.ShouldHaveSingleItem();
        request.Booking.Kind.ShouldBe(OccupancyKind.InProgress);
        request.Booking.AssignedVehicleId.ShouldBe(harness.VehicleA.Id);
        booking.Events[^1].Type.ShouldBe(BookingEventType.Extended);
    }

    [Fact]
    public async Task A_manual_discount_needs_the_permission_and_a_reason()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();
        var discount = Current(booking) with { ManualDiscount = Money.Tnd(30m), Reason = "Client fidèle" };

        (await harness.Amendments.AmendAsync(discount, Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        harness.As(Roles.Manager);
        (await harness.Amendments.AmendAsync(discount with { Reason = null }, Ct)).Error.ShouldBe(BookingErrors.ReasonRequired);
        var applied = await harness.Amendments.AmendAsync(discount, Ct);

        applied.Value.TotalInclVat.ShouldBe(Money.Tnd(277.140m));
        var quote = harness.Db.Rows<Quote>().Single(q => q.Version == 2);
        quote.Origin.ShouldBe(QuoteOrigin.ManualDiscount);
        quote.ManualDiscountReason.ShouldBe("Client fidèle");
        quote.Lines[^1].Kind.ShouldBe(QuoteLineKind.ManualDiscount);
        harness.Pricing.Requests.ShouldBeEmpty("a discount alone keeps the frozen lines");
        harness.Guard.AvailabilityChecks.ShouldBeEmpty();
        booking.Events[^1].Reason.ShouldBe("Client fidèle");
    }

    [Fact]
    public async Task The_preview_prices_without_saving()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();

        var preview = await harness.Amendments.PreviewAsync(Current(booking) with { ReturnAt = booking.ReturnAt.AddDays(1) }, Ct);

        preview.Value.CreatesQuoteVersion.ShouldBeTrue();
        preview.Value.NewTotalInclVat.ShouldBe(Money.Tnd(409.520m));
        preview.Value.CurrentTotalInclVat.ShouldBe(Money.Tnd(307.140m));
        preview.Value.IsAvailable.ShouldBe(true);
        preview.Value.NewQuote!.Lines.Count.ShouldBe(2);
        harness.UnitOfWork.Executions.ShouldBe(1, "only the arrangement");
        booking.CurrentQuoteVersion.ShouldBe(1);
    }

    [Fact]
    public async Task An_amendment_that_no_longer_fits_changes_nothing()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();
        harness.Guard.NextAvailability = AvailabilityErrors.NotAvailable;

        (await harness.Amendments.AmendAsync(Current(booking) with { ReturnAt = booking.ReturnAt.AddDays(1) }, Ct)).Error.ShouldBe(AvailabilityErrors.NotAvailable);

        booking.CurrentQuoteVersion.ShouldBe(1);
        harness.Notifier.Changed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_users_who_manage_bookings_amend_them()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();
        harness.As(Roles.Accountant);

        (await harness.Amendments.AmendAsync(Current(booking), Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        (await harness.Amendments.PreviewAsync(Current(booking), Ct)).Error.ShouldBe(BookingErrors.Forbidden);
    }
}
