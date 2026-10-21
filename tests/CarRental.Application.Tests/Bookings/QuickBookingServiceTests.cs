using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Tests.Bookings.Fakes;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Tests.Bookings;

/// <summary>Quick creation by phone or at the counter: permission, guard under the transaction, number, quote, outbox, cache.</summary>
public sealed class QuickBookingServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_confirmed_booking_is_checked_under_the_lock_numbered_priced_and_announced_after_the_commit()
    {
        var harness = new BookingsHarness();

        var created = await harness.QuickBookings.CreateAsync(harness.Quick(), Ct);

        created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.ToString() : null);
        var booking = harness.Db.Rows<Booking>().ShouldHaveSingleItem();
        booking.Status.ShouldBe(BookingStatus.Confirmed);
        booking.Number.ShouldBe("LOC-2027-000001");
        booking.MainDriverId.ShouldBe(harness.MainDriverId);
        booking.PayerCustomerId.ShouldBe(harness.Customers.IndividualCustomers[harness.MainDriverId], "the main driver pays by default");
        created.Value.TotalInclVat.ShouldBe(Money.Tnd(307.140m), "3 days at 100.000 and the contribution");

        var quote = harness.Db.Rows<Quote>().ShouldHaveSingleItem();
        quote.Status.ShouldBe(QuoteStatus.BookingVersion);
        quote.Version.ShouldBe(1);
        quote.Origin.ShouldBe(QuoteOrigin.Initial);
        quote.BookingId.ShouldBe(booking.Id);
        QuoteCode.IsValid(quote.Code).ShouldBeTrue();
        booking.CurrentQuoteId.ShouldBe(quote.Id);

        var (request, previous) = harness.Guard.AvailabilityChecks.ShouldHaveSingleItem();
        request.CategoryId.ShouldBe(harness.Ecmr.Id);
        request.Channel.ShouldBe(SalesChannel.Phone);
        request.Booking.BookingId.ShouldBe(booking.Id);
        request.Booking.Kind.ShouldBe(OccupancyKind.Planned);
        request.Booking.Rental.ShouldBe(booking.Rental);
        request.IgnoreChannelRestrictions.ShouldBeFalse();
        previous.ShouldBeNull();

        harness.Journal.Entries.ShouldBe(
            ["begin", $"guard.available {harness.Ecmr.Id}", "number", "commit", $"notify {harness.Ecmr.Id}"],
            "lock and re-check first, number at the end, the cache only after the commit");
        var message = harness.Outbox.Messages.ShouldHaveSingleItem().ShouldBeOfType<BookingStatusChanged>();
        message.Status.ShouldBe(BookingStatusInfo.Confirmed);
        message.PreviousStatus.ShouldBeNull();
        harness.Db.PublishedEvents.OfType<BookingCreated>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_option_expires_after_the_default_duration_but_never_after_the_pick_up()
    {
        var harness = new BookingsHarness();

        var option = await harness.QuickBookings.CreateAsync(harness.Quick(QuickBookingOutcome.Option), Ct);
        option.Value.Status.ShouldBe(BookingStatus.OptionHeld);
        option.Value.OptionExpiresAt.ShouldBe(BookingsHarness.Start.AddHours(48));

        var soon = harness.Quick(QuickBookingOutcome.Option) with { PickupAt = BookingsHarness.Start.AddHours(5), ReturnAt = BookingsHarness.Start.AddDays(2) };
        (await harness.QuickBookings.CreateAsync(soon, Ct)).Value.OptionExpiresAt.ShouldBe(BookingsHarness.Start.AddHours(5));

        var chosen = harness.Quick(QuickBookingOutcome.Option) with { OptionExpiresAt = BookingsHarness.Start.AddHours(6) };
        (await harness.QuickBookings.CreateAsync(chosen, Ct)).Value.OptionExpiresAt.ShouldBe(BookingsHarness.Start.AddHours(6));
        (await harness.QuickBookings.CreateAsync(chosen with { OptionExpiresAt = harness.PickupAt.AddMinutes(1) }, Ct)).Error.ShouldBe(BookingErrors.OptionExpiryInvalid);
        (await harness.QuickBookings.CreateAsync(chosen with { OptionExpiresAt = BookingsHarness.Start }, Ct)).Error.ShouldBe(BookingErrors.OptionExpiryInvalid);
    }

    [Fact]
    public async Task No_capacity_left_writes_nothing_and_tells_no_one()
    {
        var harness = new BookingsHarness();
        harness.Guard.NextAvailability = AvailabilityErrors.NotAvailable;

        var created = await harness.QuickBookings.CreateAsync(harness.Quick(), Ct);

        created.Error.ShouldBe(AvailabilityErrors.NotAvailable);
        harness.Journal.Entries.ShouldBe(["begin", $"guard.available {harness.Ecmr.Id}", "rollback"]);
        harness.Notifier.Changed.ShouldBeEmpty();
        harness.Db.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_commit_never_tells_the_cache()
    {
        var harness = new BookingsHarness();
        harness.UnitOfWork.FailNextCommit = new TimeoutException("Simulated outage.");

        await Should.ThrowAsync<TimeoutException>(() => harness.QuickBookings.CreateAsync(harness.Quick(), Ct));

        harness.Notifier.Changed.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_concurrent_update_becomes_a_business_conflict()
    {
        var harness = new BookingsHarness();
        harness.UnitOfWork.FailNextCommit = new DbUpdateConcurrencyException("Simulated conflict.");

        (await harness.QuickBookings.CreateAsync(harness.Quick(), Ct)).Error.ShouldBe(BookingErrors.ConcurrentUpdate);
        harness.Notifier.Changed.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Roles.Accountant)]
    [InlineData(Roles.Preparer)]
    public async Task Only_users_who_manage_bookings_create_them(string role)
    {
        var harness = new BookingsHarness(role);

        (await harness.QuickBookings.CreateAsync(harness.Quick(), Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        harness.UnitOfWork.Executions.ShouldBe(0);
    }

    [Fact]
    public async Task Web_requests_come_from_the_public_funnel_only()
    {
        var harness = new BookingsHarness();

        (await harness.QuickBookings.CreateAsync(harness.Quick(channel: SalesChannel.Web), Ct)).Error.ShouldBe(BookingErrors.ChannelInvalid);
    }

    [Fact]
    public async Task A_driver_who_fails_the_customer_check_gets_a_generic_refusal_before_any_price()
    {
        var harness = new BookingsHarness();
        harness.Customers.Blacklisted.Add(harness.MainDriverId);

        (await harness.QuickBookings.CreateAsync(harness.Quick(), Ct)).Error.ShouldBe(BookingErrors.CustomerCheckFailed);
        harness.Pricing.Requests.ShouldBeEmpty();
        harness.UnitOfWork.Executions.ShouldBe(0);
    }

    [Fact]
    public async Task A_price_that_changed_since_it_was_shown_is_not_saved()
    {
        var harness = new BookingsHarness();

        var created = await harness.QuickBookings.CreateAsync(harness.Quick() with { ExpectedTotalInclVat = Money.Tnd(300m) }, Ct);

        created.Error.ShouldBe(BookingErrors.QuotePriceChanged);
        harness.Db.Rows<Booking>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Pricing_errors_are_returned_as_they_are()
    {
        var harness = new BookingsHarness();
        var minimum = Error.Validation("pricing.minimum_duration_not_met", "Too short (minimum: 3 days).");
        harness.Pricing.NextError = minimum;

        (await harness.QuickBookings.CreateAsync(harness.Quick(), Ct)).Error.ShouldBe(minimum);
    }

    [Fact]
    public async Task Extras_promotion_code_drivers_and_guaranteed_vehicle_feed_the_price_and_the_occupancy()
    {
        var harness = new BookingsHarness();
        var request = harness.Quick() with
        {
            Extras = [new SelectedExtra("child_seat", 1)],
            PromoCode = "bienvenue10",
            SecondaryDriverId = harness.SecondDriverId,
            GuaranteedVehicleId = harness.VehicleA.Id,
            Notes = "Vol TU 712",
        };

        var created = await harness.QuickBookings.CreateAsync(request, Ct);

        created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.ToString() : null);
        var priced = harness.Pricing.Requests.ShouldHaveSingleItem();
        priced.Extras.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new SelectedExtra("CHILD_SEAT", 1));
        priced.DriverBirthDates.ShouldBe([new DateOnly(1988, 4, 12), new DateOnly(1992, 11, 3)]);
        priced.GuaranteedVehicleId.ShouldBe(harness.VehicleA.Id);
        harness.Guard.AvailabilityChecks.Single().Request.Booking.AssignedVehicleId.ShouldBe(harness.VehicleA.Id);
        var booking = harness.Db.Rows<Booking>().Single();
        booking.AssignedVehicleId.ShouldBe(harness.VehicleA.Id);
        booking.SecondaryDriverId.ShouldBe(harness.SecondDriverId);
        booking.PromoCode.ShouldBe("BIENVENUE10");
        booking.Notes.ShouldBe("Vol TU 712");
    }

    [Fact]
    public async Task A_booking_com_reference_is_unique_and_only_the_physical_capacity_counts()
    {
        var harness = new BookingsHarness();
        var request = harness.Quick(channel: SalesChannel.BookingCom) with { ChannelReference = "BKG-4455" };

        (await harness.QuickBookings.CreateAsync(request, Ct)).IsSuccess.ShouldBeTrue();
        harness.Guard.AvailabilityChecks.Single().Request.IgnoreChannelRestrictions.ShouldBeTrue();
        (await harness.QuickBookings.CreateAsync(request, Ct)).Error.ShouldBe(BookingErrors.ChannelReferenceTaken);
        (await harness.QuickBookings.CreateAsync(request with { Channel = SalesChannel.Phone }, Ct)).IsSuccess.ShouldBeTrue("unique per channel");
    }

    [Fact]
    public async Task Numbers_follow_each_other()
    {
        var harness = new BookingsHarness();

        var first = await harness.QuickBookings.CreateAsync(harness.Quick(), Ct);
        var second = await harness.QuickBookings.CreateAsync(harness.Quick(QuickBookingOutcome.Option), Ct);

        first.Value.Number.ShouldBe("LOC-2027-000001");
        second.Value.Number.ShouldBe("LOC-2027-000002");
    }
}
