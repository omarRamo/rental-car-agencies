using System.Text;
using System.Text.Json;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Channels.Adapters;
using CarRental.Application.Channels.Core;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Tests.Bookings.Availability;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Channels;

/// <summary>
/// The canonical core on fakes: a creation is idempotent by channel reference (a replay answers the same booking, a
/// reuse with other details is refused), unmapped codes and foreign currencies are refused clearly, a stop-sell of the
/// pick-up location refuses the channel, and the test adapter reads and writes its JSON.
/// </summary>
public sealed class ChannelCoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_replayed_creation_returns_the_same_booking_and_creates_nothing()
    {
        var core = await CoreWorld.CreateAsync();

        var first = await core.CreateAsync(core.Reservation("BDC-1"));
        var replay = await core.CreateAsync(core.Reservation("BDC-1"));

        first.IsSuccess.ShouldBeTrue(first.Error?.ToString());
        var answer = (ChannelReservationAnswer)replay.Response!;
        answer.Replayed.ShouldBeTrue();
        answer.Reservation.BookingNumber.ShouldBe(((ChannelReservationAnswer)first.Response!).Reservation.BookingNumber);
        core.Bookings.Created.ShouldBe(1);
        core.Channels.Db.Rows<ChannelBooking>().ShouldHaveSingleItem().Commission.ShouldBe(Money.Tnd(72m), "15 % of 480.000");
    }

    [Fact]
    public async Task A_reference_reused_with_other_details_is_refused()
    {
        var core = await CoreWorld.CreateAsync();
        await core.CreateAsync(core.Reservation("BDC-2"));

        var reused = await core.CreateAsync(core.Reservation("BDC-2") with { ReturnAt = core.PickupAt.AddDays(5) });

        reused.Error.ShouldBe(ChannelsErrors.ReferenceConflict);
        core.Bookings.Created.ShouldBe(1);
    }

    [Fact]
    public async Task Unmapped_codes_and_foreign_currencies_are_refused_with_clear_codes()
    {
        var core = await CoreWorld.CreateAsync();

        (await core.CreateAsync(core.Reservation("BDC-3") with { CategoryCode = "XXXX" })).Error.ShouldBe(ChannelsErrors.CategoryUnmapped);
        (await core.CreateAsync(core.Reservation("BDC-3") with { PickupLocationCode = "MIR" })).Error.ShouldBe(ChannelsErrors.LocationUnmapped);
        (await core.CreateAsync(core.Reservation("BDC-3") with { Extras = [new ChannelExtraRequest(ChannelOptionKind.Extra, "99", 1)] })).Error.ShouldBe(ChannelsErrors.ExtraUnmapped);
        (await core.CreateAsync(core.Reservation("BDC-3") with { Gross = new ChannelAmount(480m, "EUR") })).Error.ShouldBe(ChannelsErrors.CurrencyNotSupported);
        (await core.CreateAsync(core.Reservation("BDC-3") with { Gross = ChannelAmount.Tnd(480.0001m) })).Error.ShouldBe(ChannelsErrors.AmountInvalid);
        (await core.CreateAsync(core.Reservation("BDC 3"))).Error.ShouldBe(ChannelsErrors.ReferenceInvalid);
        (await core.CreateAsync(core.Reservation("BDC-3") with { Driver = new ChannelDriver(" ", "Trabelsi") })).Error.ShouldBe(ChannelsErrors.DriverInvalid);
        core.Bookings.Created.ShouldBe(0);
    }

    [Fact]
    public async Task A_stop_sell_of_the_pick_up_location_refuses_the_channel_but_not_a_manual_entry()
    {
        var core = await CoreWorld.CreateAsync();
        core.Channels.StopSell(core.CategoryId, TunisTime.DateOf(core.PickupAt), TunisTime.DateOf(core.PickupAt), core.AirportId);

        var inbound = await core.CreateAsync(core.Reservation("BDC-4"));
        var manual = await core.Core.CreateMappedAsync(
            core.Context,
            core.Reservation("BDC-4"),
            new MappedReservation(core.CategoryId, core.AirportId, core.AirportId, []),
            new ChannelEntryOptions(ChannelBookingSource.Manual, IgnoreChannelRestrictions: true),
            Ct);

        inbound.Error.ShouldBe(ChannelsErrors.StopSell);
        manual.IsSuccess.ShouldBeTrue(manual.Error?.ToString());
        core.Channels.Db.Rows<ChannelBooking>().ShouldHaveSingleItem().Source.ShouldBe(ChannelBookingSource.Manual);
    }

    [Fact]
    public async Task A_refusal_of_the_bookings_module_reaches_the_channel_and_records_nothing()
    {
        var core = await CoreWorld.CreateAsync();
        core.Bookings.NextError = Error.Conflict(ChannelBookingErrorCodes.NotAvailable, "No vehicle left.");

        var refused = await core.CreateAsync(core.Reservation("BDC-5"));

        refused.Error!.Code.ShouldBe(ChannelBookingErrorCodes.NotAvailable);
        core.Channels.Db.Rows<ChannelBooking>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancellation_is_idempotent_and_drops_the_commission()
    {
        var core = await CoreWorld.CreateAsync();
        await core.CreateAsync(core.Reservation("BDC-6"));

        var cancelled = await core.Core.CancelAsync(core.Context, new ChannelCancelRequest("BDC-6"), Ct);
        var again = await core.Core.CancelAsync(core.Context, new ChannelCancelRequest("BDC-6"), Ct);
        var unknown = await core.Core.CancelAsync(core.Context, new ChannelCancelRequest("BDC-404"), Ct);

        ((ChannelReservationAnswer)cancelled.Response!).Reservation.Status.ShouldBe(ChannelReservationStatus.Cancelled);
        again.IsSuccess.ShouldBeTrue();
        unknown.Error.ShouldBe(ChannelsErrors.BookingNotFound);
        core.Channels.Db.Rows<ChannelBooking>().Single().Commission.ShouldBe(Money.Zero);
    }

    [Fact]
    public async Task The_test_adapter_reads_its_json_and_answers_201_then_200_for_a_replay()
    {
        var core = await CoreWorld.CreateAsync();
        var adapter = new FakeChannelAdapter(new FakeChannelOutbox());
        var bound = new BoundChannelCore(core.Core, core.Context, adapter);
        var body = Encoding.UTF8.GetBytes("""
            {"reference":"FAKE-1","category":"ECMR","pickupLocation":"NBE","pickupAt":"2027-06-11T10:00:00+01:00","returnAt":"2027-06-14T10:00:00+01:00",
             "driver":{"firstName":"Sami","lastName":"Trabelsi"},"gross":{"amount":480,"currency":"TND"},"paymentMode":"prepaid"}
            """);

        var created = await adapter.HandleInboundAsync(new ChannelInboundMessage("booking-com", "POST", "/reservations", null, "application/json", body), bound, Ct);
        var replayed = await adapter.HandleInboundAsync(new ChannelInboundMessage("booking-com", "POST", "/reservations", null, "application/json", body), bound, Ct);
        var malformed = await adapter.HandleInboundAsync(new ChannelInboundMessage("booking-com", "POST", "/reservations", null, "application/json", Encoding.UTF8.GetBytes("{not json")), bound, Ct);
        var unknownRoute = await adapter.HandleInboundAsync(new ChannelInboundMessage("booking-com", "PATCH", "/reservations", null, null, ReadOnlyMemory<byte>.Empty), bound, Ct);
        var read = await adapter.HandleInboundAsync(new ChannelInboundMessage("booking-com", "GET", "/reservations/FAKE-1", null, null, ReadOnlyMemory<byte>.Empty), bound, Ct);

        created.StatusCode.ShouldBe(201);
        replayed.StatusCode.ShouldBe(200);
        var answer = JsonDocument.Parse(replayed.Body).RootElement;
        answer.GetProperty("replayed").GetBoolean().ShouldBeTrue();
        answer.GetProperty("status").GetString().ShouldBe("confirmed");
        answer.GetProperty("paymentMode").GetString().ShouldBe("prepaid");
        malformed.StatusCode.ShouldBe(400);
        malformed.Request.ShouldBeNull();
        JsonDocument.Parse(malformed.Body).RootElement.GetProperty("error").GetString().ShouldBe("channels.inbound.malformed");
        unknownRoute.StatusCode.ShouldBe(404);
        read.StatusCode.ShouldBe(200);
        core.Bookings.Created.ShouldBe(1);
    }

    [Fact]
    public async Task Availability_offers_the_mapped_categories_priced_for_the_channel_and_skips_a_stopped_location()
    {
        var core = await CoreWorld.CreateAsync();
        core.Availability.Available.Add(new AvailableCategoryInfo(core.Category, 2));
        var request = new ChannelAvailabilityRequest("NBE", null, core.PickupAt, core.PickupAt.AddDays(3));

        var offered = await core.Core.AvailabilityAsync(core.Context, request, Ct);
        core.Channels.StopSell(core.CategoryId, TunisTime.DateOf(core.PickupAt), TunisTime.DateOf(core.PickupAt), core.AirportId);
        var stopped = await core.Core.AvailabilityAsync(core.Context, request, Ct);
        var unmapped = await core.Core.AvailabilityAsync(core.Context, request with { PickupLocationCode = "DJE" }, Ct);

        var offer = ((ChannelAvailabilityAnswer)offered.Response!).Offers.ShouldHaveSingleItem();
        offer.CategoryCode.ShouldBe("ECMR");
        offer.Remaining.ShouldBe(2);
        offer.TotalInclVat.ShouldBe(360m);
        ((ChannelAvailabilityAnswer)stopped.Response!).Offers.ShouldBeEmpty();
        unmapped.Error.ShouldBe(ChannelsErrors.LocationUnmapped);
    }

    /// <summary>A connected channel (ECMR, airport NBE) on fakes, with a fake bookings module.</summary>
    private sealed class CoreWorld
    {
        private CoreWorld()
        {
            Channels = new ChannelWorld(Guid.CreateVersion7(), Clock);
            Category = Fleet.AddCategory(Channels.AgencyId, "ECMR");
            CategoryId = Category.Id;
            Channels.Channel.MapCategory(CategoryId, "ECMR", null, Channels.Stamp).IsSuccess.ShouldBeTrue();
            Channels.Channel.MapLocation(AirportId, "NBE", Channels.Stamp).IsSuccess.ShouldBeTrue();
            Channels.Channel.MapOption(ChannelMappingKind.Extra, "CHILD_SEAT", "7", Channels.Stamp).IsSuccess.ShouldBeTrue();
            Channels.Channel.ChangeState(ChannelState.Connected, Channels.Stamp).IsSuccess.ShouldBeTrue();
            Pricing.DailyPrices[CategoryId] = 120m;
            Bookings = new FakeChannelBookings(Clock);
            Core = new ChannelReservationCore(Channels.Db, Bookings, Bookings, Availability, Pricing, Channels.Reader(), Clock);
        }

        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2027, 6, 1, 8, 0, 0, TimeSpan.Zero));

        public ChannelWorld Channels { get; }

        public FakeFleetQueries Fleet { get; } = new(new CallJournal());

        public ProfileAvailability Availability { get; } = new();

        public DailyRatePricing Pricing { get; } = new();

        public FakeChannelBookings Bookings { get; }

        public ChannelReservationCore Core { get; }

        public ChannelContext Context { get; private set; } = null!;

        public VehicleCategoryInfo Category { get; }

        public static async Task<CoreWorld> CreateAsync()
        {
            var world = new CoreWorld();
            world.Context = await new ChannelContextLoader(world.Channels.Db, world.Fleet).ToContextAsync(world.Channels.Channel, CancellationToken.None);
            return world;
        }

        public Guid CategoryId { get; }

        public Guid AirportId { get; } = Guid.CreateVersion7();

        public DateTimeOffset PickupAt { get; } = TunisTime.FromLocal(new DateTime(2027, 6, 11, 10, 0, 0, DateTimeKind.Unspecified));

        public ChannelReservation Reservation(string reference) => new(
            reference,
            "ECMR",
            "NBE",
            "NBE",
            PickupAt,
            PickupAt.AddDays(3),
            new ChannelDriver("Sami", "Trabelsi", "+21620000000", "sami@example.com"),
            ChannelAmount.Tnd(480m));

        public Task<ChannelInboundOutcome> CreateAsync(ChannelReservation reservation) =>
            Core.CreateAsync(Context, reservation, new FakeChannelAdapter(new FakeChannelOutbox()), ChannelEntryOptions.Inbound, Ct);
    }

    /// <summary>The Bookings contracts the core uses, in memory: confirmed bookings by reference, the step run alongside.</summary>
    private sealed class FakeChannelBookings(TimeProvider clock) : IChannelBookings, IBookingsQueries
    {
        private readonly Dictionary<string, BookingSummaryInfo> _byReference = new(StringComparer.Ordinal);

        public int Created { get; private set; }

        public Error? NextError { get; set; }

        public async Task<Result<ChannelBookingResult>> CreateConfirmedAsync(ChannelBookingRequest request, Func<ChannelBookingResult, CancellationToken, Task<Result>>? alongside = null, CancellationToken cancellationToken = default)
        {
            if (NextError is { } error)
            {
                return error;
            }

            var outcome = ChannelBookingOutcome.AlreadyExisted;
            if (!_byReference.TryGetValue(request.ChannelReference, out var booking))
            {
                Created++;
                booking = Summary(request, BookingStatusInfo.Confirmed);
                _byReference[request.ChannelReference] = booking;
                outcome = ChannelBookingOutcome.Created;
            }

            var result = ResultOf(booking, outcome);
            return alongside is null || (await alongside(result, cancellationToken)).IsSuccess ? result : ChannelsErrors.ReferenceConflict;
        }

        public Task<Result<ChannelBookingResult>> AmendAsync(ChannelBookingAmendment amendment, Func<ChannelBookingResult, CancellationToken, Task<Result>>? alongside = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<Result<ChannelBookingResult>> CancelAsync(Guid bookingId, Func<ChannelBookingResult, CancellationToken, Task<Result>>? alongside = null, CancellationToken cancellationToken = default)
        {
            var (reference, booking) = _byReference.Single(pair => pair.Value.Id == bookingId);
            var outcome = booking.Status == BookingStatusInfo.Cancelled ? ChannelBookingOutcome.AlreadyCancelled : ChannelBookingOutcome.Cancelled;
            booking = booking with { Status = BookingStatusInfo.Cancelled };
            _byReference[reference] = booking;
            var result = ResultOf(booking, outcome);
            var step = alongside is null ? Result.Success() : await alongside(result, cancellationToken);
            return step.IsSuccess ? result : step.Error;
        }

        public Task<BookingSummaryInfo?> FindByChannelReferenceAsync(Guid agencyId, SalesChannel channel, string channelReference, CancellationToken cancellationToken = default) =>
            Task.FromResult(_byReference.GetValueOrDefault(channelReference));

        public Task<BookingSummaryInfo?> GetSummaryAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_byReference.Values.FirstOrDefault(b => b.Id == bookingId));

        public Task<BookingSummaryInfo?> FindByNumberAsync(Guid? agencyId, string number, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PersonBookingInfo>> GetBookingsOfPersonAsync(Guid personId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> HasOpenBookingsAsync(Guid personId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DayAgendaInfo> GetDayAgendaAsync(Guid agencyId, DateOnly date, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PendingRequestInfo>> GetPendingRequestsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PlanningInfo> GetPlanningAsync(Guid agencyId, Period range, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<LateReturnInfo>> GetLateReturnsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<BookingSummaryInfo>> GetUnassignedStartingWithinAsync(Guid agencyId, TimeSpan within, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CapacityShortfallInfo>> GetCapacityShortfallsAsync(Guid agencyId, Period range, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private static ChannelBookingResult ResultOf(BookingSummaryInfo booking, ChannelBookingOutcome outcome) =>
            new(booking.Id, booking.Number, booking.AgencyId, booking.Status, booking.ChannelReference, booking.CategoryId, booking.PickupAt, booking.ReturnAt, booking.TotalInclVat, outcome);

        private BookingSummaryInfo Summary(ChannelBookingRequest request, BookingStatusInfo status) => new(
            Guid.CreateVersion7(),
            $"LOC-2027-{Created:D6}",
            request.AgencyId,
            status,
            request.Channel,
            request.ChannelReference,
            request.CategoryId,
            request.PickupLocationId,
            request.ReturnLocationId,
            request.PickupAt,
            request.ReturnAt,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Guid.CreateVersion7(),
            1,
            Money.Tnd(447m),
            Money.Tnd(600m),
            null,
            null,
            null,
            null,
            clock.GetUtcNow(),
            clock.GetUtcNow());
    }
}
