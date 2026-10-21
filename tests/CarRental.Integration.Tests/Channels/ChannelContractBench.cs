using System.Diagnostics;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Channels.Administration;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Common.Security;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using CarRental.Integration.Tests.Bookings;
using static CarRental.Integration.Tests.Bookings.BookingWorld;

namespace CarRental.Integration.Tests.Channels;

/// <summary>A booking as the bench sends it, whatever the format of the adapter.</summary>
public sealed record BenchBooking(
    string Reference,
    string Category,
    string Location,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    decimal Gross,
    string FirstName = "Sami",
    string LastName = "Trabelsi");

/// <summary>What the channel received, read back from the adapter's format.</summary>
public sealed record BenchAnswer(int HttpStatus, string? ErrorCode, string? Status, string? BookingNumber, bool Replayed, IReadOnlyList<BenchOffer> Offers)
{
    public bool Accepted => ErrorCode is null && HttpStatus is >= 200 and < 300;
}

/// <summary>An offer of an availability answer.</summary>
public sealed record BenchOffer(string Category, int Remaining, decimal Total);

/// <summary>Speaks the format of one adapter to the real endpoint (the fake JSON today, the Booking.com format tomorrow).</summary>
public interface IChannelBenchDriver
{
    Task<BenchAnswer> AvailabilityAsync(string location, DateTimeOffset pickupAt, DateTimeOffset returnAt, string? category, CancellationToken cancellationToken);

    Task<BenchAnswer> CreateAsync(BenchBooking booking, CancellationToken cancellationToken);

    Task<BenchAnswer> ModifyAsync(BenchBooking booking, CancellationToken cancellationToken);

    Task<BenchAnswer> CancelAsync(string reference, CancellationToken cancellationToken);

    Task<BenchAnswer> ReadAsync(string reference, CancellationToken cancellationToken);
}

/// <summary>
/// The contract bench of the channel adapters (docs/conception/phase-5-canaux.md §4), on SQL Server through the real
/// endpoint: confirmed creation then replay without duplicate, modification of the dates under the availability check,
/// cancellation, reading, availability under a stop-sell and a quota, unmapped codes refused clearly, the last vehicle
/// asked by two channels at once (only one gets it), and the response time of availability. A derived class runs it
/// on one adapter: the test adapter today, the Booking.com adapter once its specification is delivered.
/// </summary>
public abstract class ChannelContractBench(IntegrationHost host)
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected IntegrationHost Host => host;

    /// <summary>The adapter under test.</summary>
    protected abstract ChannelAdapterKind Adapter { get; }

    /// <summary>The driver that speaks the adapter's format for <paramref name="channel"/>.</summary>
    private protected abstract IChannelBenchDriver DriverFor(TestChannel channel);

    [Fact]
    public async Task A_created_booking_is_confirmed_in_the_planning_and_a_replay_creates_no_duplicate()
    {
        var channel = await ConnectAsync(ecmrVehicles: 2);
        var driver = DriverFor(channel);
        var booking = new BenchBooking(Reference(), "ECMR", TestChannel.OfficeCode, InDays(30), InDays(33), 480m);

        var created = await driver.CreateAsync(booking, Ct);
        var replayed = await driver.CreateAsync(booking, Ct);

        created.Accepted.ShouldBeTrue(created.ToString());
        created.Status.ShouldBe("confirmed");
        created.Replayed.ShouldBeFalse();
        replayed.Accepted.ShouldBeTrue(replayed.ToString());
        replayed.Replayed.ShouldBeTrue();
        replayed.BookingNumber.ShouldBe(created.BookingNumber);

        var summary = (await BookingOfAsync(channel, booking.Reference)).ShouldNotBeNull();
        summary.Status.ShouldBe(BookingStatusInfo.Confirmed);
        summary.Number.ShouldBe(created.BookingNumber);
        summary.Channel.ShouldBe(SalesChannel.BookingCom);
        summary.CategoryId.ShouldBe(channel.Agency.Ecmr);
        var side = (await ChannelSideAsync(summary.Id)).ShouldNotBeNull();
        side.Gross.ShouldBe(Money.Tnd(480m));
        side.Commission.ShouldBe(Money.Tnd(72m));
        side.Net.ShouldBe(Money.Tnd(408m));
        side.PaymentMode.ShouldBe(ChannelPaymentModeInfo.PayOnSite);
        side.Source.ShouldBe(ChannelBookingSourceInfo.Inbound);

        var planning = await RunAsync<IBookingsQueries, PlanningInfo>(host, Manager, q => q.GetPlanningAsync(channel.Agency.AgencyId, Period.Create(InDays(29), InDays(35)).Value, Ct));
        planning.Categories.Single(c => c.Category.Id == channel.Agency.Ecmr).Unassigned.ShouldContain(b => b.BookingId == summary.Id, "a channel booking appears in the planning without intervention");
    }

    [Fact]
    public async Task A_modification_of_the_dates_is_checked_against_the_availability()
    {
        var channel = await ConnectAsync(ecmrVehicles: 1);
        var driver = DriverFor(channel);
        var booking = new BenchBooking(Reference(), "ECMR", TestChannel.OfficeCode, InDays(40), InDays(42), 320m);
        (await driver.CreateAsync(booking, Ct)).Accepted.ShouldBeTrue();
        var blocker = await NewDriverAsync(host, Ct);
        await BookingWorld.CreateAsync(host, Quick(channel.Agency, blocker, InDays(50), InDays(52)), Ct);

        var moved = await driver.ModifyAsync(booking with { PickupAt = InDays(44), ReturnAt = InDays(47), Gross = 480m }, Ct);
        var refused = await driver.ModifyAsync(booking with { PickupAt = InDays(50), ReturnAt = InDays(51) }, Ct);

        moved.Accepted.ShouldBeTrue(moved.ToString());
        refused.ErrorCode.ShouldBe(ChannelBookingErrorCodes.NotAvailable);
        var summary = (await BookingOfAsync(channel, booking.Reference)).ShouldNotBeNull();
        summary.PickupAt.ShouldBe(InDays(44));
        summary.ReturnAt.ShouldBe(InDays(47));
        (await ChannelSideAsync(summary.Id))!.Gross.ShouldBe(Money.Tnd(480m));
    }

    [Fact]
    public async Task A_cancellation_releases_the_vehicle_and_is_idempotent()
    {
        var channel = await ConnectAsync(ecmrVehicles: 1);
        var driver = DriverFor(channel);
        var booking = new BenchBooking(Reference(), "ECMR", TestChannel.OfficeCode, InDays(60), InDays(63), 450m);
        (await driver.CreateAsync(booking, Ct)).Accepted.ShouldBeTrue();
        (await driver.AvailabilityAsync(TestChannel.OfficeCode, InDays(60), InDays(63), "ECMR", Ct)).Offers.ShouldBeEmpty("the only vehicle is taken");

        var cancelled = await driver.CancelAsync(booking.Reference, Ct);
        var again = await driver.CancelAsync(booking.Reference, Ct);
        var unknown = await driver.CancelAsync(Reference(), Ct);

        cancelled.Accepted.ShouldBeTrue(cancelled.ToString());
        cancelled.Status.ShouldBe("cancelled");
        again.Accepted.ShouldBeTrue("a cancellation is idempotent");
        unknown.ErrorCode.ShouldBe(ChannelsErrors.BookingNotFound.Code);
        var summary = (await BookingOfAsync(channel, booking.Reference)).ShouldNotBeNull();
        summary.Status.ShouldBe(BookingStatusInfo.Cancelled);
        (await ChannelSideAsync(summary.Id))!.Commission.ShouldBe(Money.Zero);
        (await driver.AvailabilityAsync(TestChannel.OfficeCode, InDays(60), InDays(63), "ECMR", Ct)).Offers.ShouldHaveSingleItem().Remaining.ShouldBe(1);
    }

    [Fact]
    public async Task A_booking_is_read_by_its_reference()
    {
        var channel = await ConnectAsync(ecmrVehicles: 1);
        var driver = DriverFor(channel);
        var booking = new BenchBooking(Reference(), "CDMR", TestChannel.AirportCode, InDays(70), InDays(72), 330m);
        var created = await driver.CreateAsync(booking, Ct);

        var read = await driver.ReadAsync(booking.Reference, Ct);
        var unknown = await driver.ReadAsync(Reference(), Ct);

        read.Accepted.ShouldBeTrue(read.ToString());
        read.BookingNumber.ShouldBe(created.BookingNumber);
        read.Status.ShouldBe("confirmed");
        unknown.HttpStatus.ShouldBe(404);
        unknown.ErrorCode.ShouldBe(ChannelsErrors.BookingNotFound.Code);
    }

    [Fact]
    public async Task Availability_honours_a_stop_sell_and_a_quota_of_the_channel_only()
    {
        var channel = await ConnectAsync(ecmrVehicles: 2);
        var driver = DriverFor(channel);
        var stopDay = TunisTime.DateOf(InDays(80));
        var quotaDay = TunisTime.DateOf(InDays(90));
        await RestrictAsync(channel, new RestrictionRequest(ChannelQuotaKind.StopSell, channel.Agency.Ecmr, null, stopDay, stopDay.AddDays(1), 0, "Haute saison"));
        await RestrictAsync(channel, new RestrictionRequest(ChannelQuotaKind.Reserve, channel.Agency.Ecmr, null, quotaDay, quotaDay.AddDays(2), 1, null));

        var stopped = await driver.AvailabilityAsync(TestChannel.OfficeCode, InDays(80), InDays(81), null, Ct);
        var quota = await driver.AvailabilityAsync(TestChannel.OfficeCode, InDays(90), InDays(91), null, Ct);
        var website = await RunAsync<IAvailabilityQueries, CategoryAvailabilityInfo?>(host, Manager, q => q.GetCategoryAvailabilityAsync(channel.Agency.Ecmr, Period.Create(InDays(80), InDays(81)).Value, SalesChannel.Web, cancellationToken: Ct));
        var websiteQuota = await RunAsync<IAvailabilityQueries, CategoryAvailabilityInfo?>(host, Manager, q => q.GetCategoryAvailabilityAsync(channel.Agency.Ecmr, Period.Create(InDays(90), InDays(91)).Value, SalesChannel.Web, cancellationToken: Ct));

        stopped.Accepted.ShouldBeTrue(stopped.ToString());
        stopped.Offers.Select(o => o.Category).ShouldBe(["CDMR"], "the stop-sell removes ECMR from the channel only");
        quota.Offers.Single(o => o.Category == "ECMR").Remaining.ShouldBe(1, "one of the two vehicles is kept for the other channels");
        website!.Remaining.ShouldBe(2, "a stop-sell of the channel never blocks the website");
        websiteQuota!.Remaining.ShouldBe(2);
    }

    [Fact]
    public async Task Unmapped_category_or_location_is_refused_with_a_clear_code()
    {
        var channel = await ConnectAsync(ecmrVehicles: 1);
        var driver = DriverFor(channel);

        var category = await driver.CreateAsync(new BenchBooking(Reference(), "XDAR", TestChannel.OfficeCode, InDays(100), InDays(102), 300m), Ct);
        var location = await driver.CreateAsync(new BenchBooking(Reference(), "ECMR", "DJE", InDays(100), InDays(102), 300m), Ct);
        var availability = await driver.AvailabilityAsync("MIR", InDays(100), InDays(102), null, Ct);

        category.ErrorCode.ShouldBe(ChannelsErrors.CategoryUnmapped.Code);
        location.ErrorCode.ShouldBe(ChannelsErrors.LocationUnmapped.Code);
        availability.ErrorCode.ShouldBe(ChannelsErrors.LocationUnmapped.Code);
        category.HttpStatus.ShouldBe(422);
    }

    [Fact]
    public async Task Two_channels_asking_for_the_last_vehicle_at_once_only_one_gets_it()
    {
        var channel = await ConnectAsync(ecmrVehicles: 1);
        var driver = DriverFor(channel);
        var webDriver = await NewDriverAsync(host, Ct);
        var pickup = InDays(110);

        // The channel and the website at once.
        var fromChannel = driver.CreateAsync(new BenchBooking(Reference(), "ECMR", TestChannel.OfficeCode, pickup, pickup.AddDays(3), 480m), Ct);
        var fromWebsite = RunAsync<IBookingRequests, Result<BookingRequestResult>>(
            host,
            UserContextSnapshot.System("system:public-funnel"),
            s => s.SubmitRequestAsync(
                new BookingRequestDraft(SalesChannel.Web, Price: new PriceRequest(channel.Agency.AgencyId, channel.Agency.Ecmr, channel.Agency.OfficeId, channel.Agency.OfficeId, pickup.AddDays(1), pickup.AddDays(4), SalesChannel.Web)),
                new BookingRequestParties(webDriver),
                Ct));
        await Task.WhenAll(fromChannel, fromWebsite);

        // Two references of the channel at once, on another period.
        var later = InDays(120);
        var first = driver.CreateAsync(new BenchBooking(Reference(), "ECMR", TestChannel.OfficeCode, later, later.AddDays(2), 320m), Ct);
        var second = driver.CreateAsync(new BenchBooking(Reference(), "ECMR", TestChannel.AirportCode, later.AddHours(5), later.AddDays(3), 400m), Ct);
        await Task.WhenAll(first, second);

        var channelWon = (await fromChannel).Accepted;
        var websiteWon = (await fromWebsite).IsSuccess;
        (channelWon ^ websiteWon).ShouldBeTrue($"exactly one: channel {await fromChannel}, website {await fromWebsite}");
        if (!channelWon)
        {
            (await fromChannel).ErrorCode.ShouldBe(ChannelBookingErrorCodes.NotAvailable);
        }

        BenchAnswer[] references = [await first, await second];
        references.Count(a => a.Accepted).ShouldBe(1, string.Join(" | ", references.Select(a => a.ToString())));
        references.Single(a => !a.Accepted).ErrorCode.ShouldBe(ChannelBookingErrorCodes.NotAvailable);
    }

    [Fact]
    public async Task Availability_answers_in_less_than_a_second()
    {
        var channel = await ConnectAsync(ecmrVehicles: 2);
        var driver = DriverFor(channel);
        (await driver.AvailabilityAsync(TestChannel.OfficeCode, InDays(130), InDays(133), null, Ct)).Accepted.ShouldBeTrue("warm-up");

        var timings = new List<TimeSpan>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var watch = Stopwatch.StartNew();
            var answer = await driver.AvailabilityAsync(TestChannel.OfficeCode, InDays(130 + attempt), InDays(133 + attempt), null, Ct);
            watch.Stop();
            answer.Offers.Count.ShouldBe(2);
            timings.Add(watch.Elapsed);
        }

        // Median under the SPEC's one second (a single slow answer under the parallel test load does not fail the bench).
        timings.Order().ElementAt(timings.Count / 2).ShouldBeLessThan(TimeSpan.FromSeconds(1), string.Join(", ", timings.Select(t => $"{t.TotalMilliseconds:F0} ms")));
    }

    protected static string Reference() => "BENCH-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private protected Task<TestChannel> ConnectAsync(int ecmrVehicles) => ChannelTestWorld.CreateAsync(host, Ct, ecmrVehicles, Adapter);

    private protected Task<BookingSummaryInfo?> BookingOfAsync(TestChannel channel, string reference) =>
        RunAsync<IChannelBookings, BookingSummaryInfo?>(host, Manager, b => b.FindByChannelReferenceAsync(SalesChannel.BookingCom, reference, Ct));

    protected Task<ChannelBookingInfo?> ChannelSideAsync(Guid bookingId) =>
        RunAsync<IChannelsQueries, ChannelBookingInfo?>(host, Manager, q => q.GetChannelBookingAsync(bookingId, Ct));

    private async Task RestrictAsync(TestChannel channel, RestrictionRequest request)
    {
        var added = await ChannelTestWorld.RunAsync<ChannelRestrictionService, Result<Guid>>(host, s => s.AddAsync(channel.Id, request, Ct));
        added.IsSuccess.ShouldBeTrue(added.IsFailure ? added.Error.ToString() : null);
    }
}

/// <summary>The contract bench on the TEST-ONLY JSON adapter.</summary>
public sealed class FakeChannelContractTests(IntegrationHost host) : ChannelContractBench(host)
{
    protected override ChannelAdapterKind Adapter => ChannelAdapterKind.Fake;

    private protected override IChannelBenchDriver DriverFor(TestChannel channel) => new FakeBenchDriver(new FakeChannelClient(Host.CreateClient(), channel));

    /// <summary>Speaks the fake JSON format.</summary>
    private sealed class FakeBenchDriver(FakeChannelClient client) : IChannelBenchDriver
    {
        public async Task<BenchAnswer> AvailabilityAsync(string location, DateTimeOffset pickupAt, DateTimeOffset returnAt, string? category, CancellationToken cancellationToken) =>
            await ReadAsync(await client.AvailabilityAsync(location, pickupAt, returnAt, category, cancellationToken), cancellationToken);

        public async Task<BenchAnswer> CreateAsync(BenchBooking booking, CancellationToken cancellationToken) =>
            await ReadAsync(await client.CreateAsync(Body(booking), cancellationToken), cancellationToken);

        public async Task<BenchAnswer> ModifyAsync(BenchBooking booking, CancellationToken cancellationToken) =>
            await ReadAsync(await client.ModifyAsync(booking.Reference, Body(booking), cancellationToken), cancellationToken);

        public async Task<BenchAnswer> CancelAsync(string reference, CancellationToken cancellationToken) =>
            await ReadAsync(await client.CancelAsync(reference, cancellationToken), cancellationToken);

        public async Task<BenchAnswer> ReadAsync(string reference, CancellationToken cancellationToken) =>
            await ReadAsync(await client.ReadAsync(reference, cancellationToken), cancellationToken);

        private static object Body(BenchBooking booking) =>
            FakeChannelClient.Reservation(booking.Reference, booking.PickupAt, booking.ReturnAt, booking.Category, booking.Location, booking.Gross, firstName: booking.FirstName, lastName: booking.LastName);

        private static async Task<BenchAnswer> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            using (response)
            {
                var json = await FakeChannelClient.JsonAsync(response, cancellationToken);
                var offers = json.TryGetProperty("offers", out var list)
                    ? list.EnumerateArray().Select(o => new BenchOffer(o.GetProperty("category").GetString()!, o.GetProperty("remaining").GetInt32(), o.GetProperty("total").GetDecimal())).ToList()
                    : [];
                return new BenchAnswer(
                    (int)response.StatusCode,
                    json.TryGetProperty("error", out var error) ? error.GetString() : null,
                    json.TryGetProperty("status", out var status) ? status.GetString() : null,
                    json.TryGetProperty("bookingNumber", out var number) ? number.GetString() : null,
                    json.TryGetProperty("replayed", out var replayed) && replayed.GetBoolean(),
                    offers);
            }
        }
    }
}
