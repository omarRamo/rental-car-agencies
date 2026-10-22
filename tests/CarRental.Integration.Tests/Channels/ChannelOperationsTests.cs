using System.Net;
using System.Text.Json;
using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Channels.Adapters;
using CarRental.Application.Channels.Administration;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Common.Paging;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Bookings.BookingWorld;

namespace CarRental.Integration.Tests.Channels;

/// <summary>
/// The channels in operation on SQL Server: manual entry of a Booking.com booking (confirmed in the planning, idempotent,
/// never beyond the physical capacity), reminders to replicate on the extranet, updates pushed to a connected channel
/// after a stop-sell or a booking, a refused push replayed from the screen, the health entry, the commissions for the
/// reporting, the monthly reconciliation and a cancellation at the counter mirrored on the channel side.
/// </summary>
public sealed class ChannelOperationsTests(IntegrationHost host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_manager_enters_a_booking_com_booking_by_hand_and_it_is_confirmed_in_the_planning()
    {
        var channel = await ChannelTestWorld.CreateAsync(host, Ct, ecmrVehicles: 1, adapter: ChannelAdapterKind.Manual, state: ChannelState.Manual);
        var reference = "BDC" + Random.Shared.Next(10_000_000, 99_999_999);
        await RestrictAsync(channel, new RestrictionRequest(ChannelQuotaKind.StopSell, channel.Agency.Ecmr, null, TunisTime.DateOf(InDays(45)), TunisTime.DateOf(InDays(48)), 0, null));
        var entry = Entry(channel, reference, InDays(45), InDays(48));

        var preview = await ManualAsync(s => s.PreviewAsync(entry, Ct));
        var recorded = await ManualAsync(s => s.RecordAsync(entry, Ct));
        var replayed = await ManualAsync(s => s.RecordAsync(entry, Ct));

        preview.Value.Commission.ShouldBe(Money.Tnd(90m), "15 % of 600.000");
        preview.Value.Net.ShouldBe(Money.Tnd(510m));
        preview.Value.EngineTotalInclVat.ShouldNotBeNull();
        recorded.IsSuccess.ShouldBeTrue(recorded.IsFailure ? recorded.Error.ToString() : null);
        recorded.Value.Replayed.ShouldBeFalse();
        replayed.Value.Replayed.ShouldBeTrue();
        replayed.Value.BookingId.ShouldBe(recorded.Value.BookingId);

        var summary = await RunAsync<IBookingsQueries, BookingSummaryInfo?>(host, Manager, q => q.GetSummaryAsync(recorded.Value.BookingId, Ct));
        summary.ShouldNotBeNull().Status.ShouldBe(BookingStatusInfo.Confirmed, "the stop-sell of the channel does not block a booking already sold on its extranet");
        summary.ChannelReference.ShouldBe(reference);
        var side = (await RunAsync<IChannelsQueries, ChannelBookingInfo?>(host, Manager, q => q.GetChannelBookingAsync(summary.Id, Ct))).ShouldNotBeNull();
        side.Source.ShouldBe(ChannelBookingSourceInfo.Manual);
        side.PaymentMode.ShouldBe(ChannelPaymentModeInfo.PrepaidToChannel);
        side.Gross.ShouldBe(Money.Tnd(600m));
        side.Commission.ShouldBe(Money.Tnd(90m));
        var planning = await RunAsync<IBookingsQueries, PlanningInfo>(host, Manager, q => q.GetPlanningAsync(channel.Agency.AgencyId, Period.Create(InDays(44), InDays(50)).Value, Ct));
        planning.Categories.Single(c => c.Category.Id == channel.Agency.Ecmr).Unassigned.ShouldContain(b => b.BookingId == summary.Id);

        // The only vehicle is taken: another entry on the same dates is refused, never an overbooking.
        var overbooking = await ManualAsync(s => s.RecordAsync(Entry(channel, reference + "9", InDays(46), InDays(47)), Ct));
        overbooking.Error.Code.ShouldBe(ChannelBookingErrorCodes.NotAvailable);
    }

    [Fact]
    public async Task Restrictions_of_a_manual_channel_wait_on_the_extranet_list_until_acknowledged()
    {
        var channel = await ChannelTestWorld.CreateAsync(host, Ct, adapter: ChannelAdapterKind.Manual, state: ChannelState.Manual);
        var day = TunisTime.DateOf(InDays(55));
        var quotaId = await RestrictAsync(channel, new RestrictionRequest(ChannelQuotaKind.Reserve, channel.Agency.Ecmr, null, day, day.AddDays(10), 1, "Haute saison"));
        await RestrictAsync(channel, new RestrictionRequest(ChannelQuotaKind.StopSell, channel.Agency.Cdmr, channel.Agency.AirportId, day, day, 0, null));
        var removed = await ChannelTestWorld.RunAsync<ChannelRestrictionService, Result>(host, s => s.RemoveAsync(quotaId, Ct));
        removed.IsSuccess.ShouldBeTrue();

        var pending = (await ManualAsync(s => s.GetRemindersAsync(channel.Id, cancellationToken: Ct))).Value;
        var acknowledged = await ManualAsync(s => s.AcknowledgeAsync(channel.Id, [], Ct));
        var after = (await ManualAsync(s => s.GetRemindersAsync(channel.Id, cancellationToken: Ct))).Value;

        pending.Select(r => r.Kind).ShouldBe([ChannelReplicationKind.QuotaAdded, ChannelReplicationKind.StopSellAdded, ChannelReplicationKind.QuotaRemoved]);
        pending.Single(r => r.Kind == ChannelReplicationKind.StopSellAdded).LocationCode.ShouldNotBeNull();
        pending[0].Vehicles.ShouldBe(1);
        acknowledged.Value.ShouldBe(3);
        after.ShouldBeEmpty();
        (await ChannelTestWorld.RunAsync<ChannelRestrictionService, Result<IReadOnlyList<RestrictionView>>>(host, s => s.ListAsync(channel.Id, cancellationToken: Ct))).Value
            .ShouldHaveSingleItem().Kind.ShouldBe(ChannelQuotaKind.StopSell);
    }

    [Fact]
    public async Task A_stop_sell_and_a_booking_are_pushed_to_a_connected_channel_by_the_pump()
    {
        var channel = await ChannelTestWorld.CreateAsync(host, Ct, ecmrVehicles: 2);
        var outbox = host.Factory.Services.GetRequiredService<FakeChannelOutbox>();
        var day = TunisTime.DateOf(InDays(65));

        await RestrictAsync(channel, new RestrictionRequest(ChannelQuotaKind.StopSell, channel.Agency.Ecmr, null, day, day, 0, null));
        var stopped = await WaitForAsync(() => outbox.ReceivedBy(channel.Code).Any(p => Has(p, "ECMR", day, 0)));
        var driver = await NewDriverAsync(host, Ct);
        await CreateAsync(host, Quick(channel.Agency, driver, InDays(75), InDays(77)), Ct);
        var booked = await WaitForAsync(() => outbox.ReceivedBy(channel.Code).Any(p => Has(p, "ECMR", TunisTime.DateOf(InDays(76)), 1)));

        stopped.ShouldBeTrue("the stop-sell reaches the channel: ECMR at 0 that day");
        booked.ShouldBeTrue("the phone booking reaches the channel: one ECMR left");
        outbox.ReceivedBy(channel.Code).ShouldAllBe(p => p.Operation == ChannelSyncOperation.PushAvailability || p.Operation == ChannelSyncOperation.PushRates);
    }

    [Fact]
    public async Task A_refused_push_is_kept_with_its_error_and_sent_again_from_the_screen()
    {
        var channel = await ChannelTestWorld.CreateAsync(host, Ct);
        var outbox = host.Factory.Services.GetRequiredService<FakeChannelOutbox>();
        outbox.FailNext(channel.Code, 1);
        await RestrictAsync(channel, new RestrictionRequest(ChannelQuotaKind.StopSell, channel.Agency.Cdmr, null, TunisTime.DateOf(InDays(85)), TunisTime.DateOf(InDays(85)), 0, null));

        SyncItemEntry? failed = null;
        (await WaitForAsync(async () =>
        {
            failed = (await ItemsAsync(channel)).FirstOrDefault(i => i.Attempts >= 1 && i.LastError is not null && i.Status == ChannelSyncStatus.Pending);
            return failed is not null;
        })).ShouldBeTrue("the first attempt fails and waits for its retry");
        failed!.LastError.ShouldNotBeNull().ShouldStartWith(ChannelsErrors.PushFailed.Code);
        failed.NextAttemptAtUtc.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(5));

        var replay = await ChannelTestWorld.RunAsync<ChannelMonitoringService, Result>(host, s => s.ReplayAsync(failed.Id, Ct));
        replay.IsSuccess.ShouldBeTrue();
        (await WaitForAsync(async () => (await ItemsAsync(channel)).Any(i => i.Id == failed.Id && i.Status == ChannelSyncStatus.Sent))).ShouldBeTrue("the replay sends it at once");
        var log = (await ChannelTestWorld.RunAsync<ChannelMonitoringService, Result<PagedResult<MessageLogEntry>>>(
            host,
            s => s.ListMessagesAsync(new MessageLogFilter(ChannelId: channel.Id, Direction: ChannelMessageDirection.Outbound), new PageRequest(1, 50), Ct))).Value.Items;
        log.ShouldContain(m => m.Status == ChannelMessageStatus.Failed && m.ErrorCode == ChannelsErrors.PushFailed.Code);
        log.ShouldContain(m => m.Status == ChannelMessageStatus.Accepted);
    }

    [Fact]
    public async Task An_update_failed_for_good_turns_the_channels_readiness_entry_degraded()
    {
        var channel = await ChannelTestWorld.CreateAsync(host, Ct, state: ChannelState.Manual);
        var itemId = await InsertFailedItemAsync(channel);
        try
        {
            using var client = host.CreateClient();

            var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
            var entry = JsonDocument.Parse(body).RootElement.GetProperty("checks").GetProperty("channels");
            entry.GetProperty("status").GetString().ShouldBe("Degraded", body);
            entry.GetProperty("description").GetString().ShouldNotBeNull().ShouldContain("failed for good");
        }
        finally
        {
            // The channel is manual: the replayed item is skipped at once by the next run.
            (await ChannelTestWorld.RunAsync<ChannelMonitoringService, Result>(host, s => s.ReplayAsync(itemId, Ct))).IsSuccess.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Commissions_reach_the_reporting_and_the_reconciliation_compares_the_payout()
    {
        var channel = await ChannelTestWorld.CreateAsync(host, Ct, ecmrVehicles: 2);
        using var http = host.CreateClient();
        var client = new FakeChannelClient(http, channel);
        var pickup = InDays(95);
        var prepaid = new
        {
            reference = "REC-" + Guid.NewGuid().ToString("N")[..8],
            category = "ECMR",
            pickupLocation = TestChannel.OfficeCode,
            pickupAt = ChannelTestWorld.Iso(pickup),
            returnAt = ChannelTestWorld.Iso(pickup.AddDays(2)),
            driver = new { firstName = "Amel", lastName = "Ben Salah" },
            gross = new { amount = 400m, currency = "TND" },
            commission = new { amount = 50m, currency = "TND" },
            paymentMode = "prepaid",
        };
        (await client.CreateAsync(prepaid, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await client.CreateAsync(FakeChannelClient.Reservation("REC-" + Guid.NewGuid().ToString("N")[..8], pickup.AddHours(3), pickup.AddDays(3), category: "CDMR", gross: 300m), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var day = TunisTime.DateOf(pickup);
        var commissions = await RunAsync<IChannelCommissions, IReadOnlyList<ChannelCommissionInfo>>(host, Manager, c => c.GetCommissionsAsync(day, day, channel.Agency.AgencyId, Ct));
        var byBooking = await RunAsync<IChannelCommissions, IReadOnlyDictionary<Guid, Money>>(host, Manager, c => c.GetCommissionsByBookingAsync(channel.Agency.AgencyId, [.. commissions.Select(x => x.BookingId), Guid.CreateVersion7()], Ct));
        var payout = await ChannelTestWorld.RunAsync<ChannelReconciliationService, Result>(host, s => s.RecordPayoutAsync(channel.Id, day.Year, day.Month, 300m, "VIR-1", null, Ct));
        var month = (await ChannelTestWorld.RunAsync<ChannelReconciliationService, Result<ReconciliationView>>(host, s => s.GetMonthAsync(channel.Id, day.Year, day.Month, Ct))).Value;

        commissions.Count.ShouldBe(2);
        commissions.Single(c => c.PaymentMode == ChannelPaymentModeInfo.PrepaidToChannel).Commission.ShouldBe(Money.Tnd(50m), "the amount reported by the channel wins");
        commissions.Single(c => c.PaymentMode == ChannelPaymentModeInfo.PayOnSite).Commission.ShouldBe(Money.Tnd(45m), "15 % of 300.000");
        byBooking.Count.ShouldBe(2);
        payout.IsSuccess.ShouldBeTrue();
        month.Figures.PrepaidNet.ShouldBe(Money.Tnd(350m));
        month.Figures.PayOnSiteCommission.ShouldBe(Money.Tnd(45m));
        month.Figures.ExpectedSettlement.ShouldBe(Money.Tnd(305m));
        month.Figures.Difference.ShouldBe(Money.Tnd(-5m));
        month.Bookings.Count.ShouldBe(2);
        month.PayoutReference.ShouldBe("VIR-1");
    }

    [Fact]
    public async Task A_channel_booking_cancelled_at_the_counter_owes_no_commission()
    {
        var channel = await ChannelTestWorld.CreateAsync(host, Ct);
        using var http = host.CreateClient();
        var reference = "CNT-" + Guid.NewGuid().ToString("N")[..8];
        (await new FakeChannelClient(http, channel).CreateAsync(FakeChannelClient.Reservation(reference, InDays(105), InDays(107)), Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var booking = (await RunAsync<IChannelBookings, BookingSummaryInfo?>(host, Manager, b => b.FindByChannelReferenceAsync(SalesChannel.BookingCom, reference, Ct))).ShouldNotBeNull();

        var cancelled = await RunAsync<BookingWorkflowService, Result<Money>>(host, Agent, s => s.CancelAsync(booking.Id, "Le client a appelé l'agence", cancellationToken: Ct));

        cancelled.IsSuccess.ShouldBeTrue(cancelled.IsFailure ? cancelled.Error.ToString() : null);
        (await WaitForAsync(async () => (await RunAsync<IChannelsQueries, ChannelBookingInfo?>(host, Manager, q => q.GetChannelBookingAsync(booking.Id, Ct)))?.State == ChannelBookingStateInfo.Cancelled))
            .ShouldBeTrue("the outbox mirrors the cancellation on the channel side");
        (await RunAsync<IChannelsQueries, ChannelBookingInfo?>(host, Manager, q => q.GetChannelBookingAsync(booking.Id, Ct)))!.Commission.ShouldBe(Money.Zero);
    }

    private static ManualEntryRequest Entry(TestChannel channel, string reference, DateTimeOffset pickupAt, DateTimeOffset returnAt) => new(
        channel.Id,
        reference,
        channel.Agency.Ecmr,
        channel.Agency.AirportId,
        channel.Agency.AirportId,
        pickupAt,
        returnAt,
        "Karim",
        ChannelTestWorld.Name("Mansour"),
        "+33 6 12 34 56 78",
        null,
        600m,
        null,
        ChannelPaymentMode.PrepaidToChannel,
        "Vol TU 721");

    private static bool Has(FakeChannelPush push, string category, DateOnly day, int available)
    {
        if (push.Operation != ChannelSyncOperation.PushAvailability)
        {
            return false;
        }

        var update = JsonSerializer.Deserialize<ChannelAvailabilityUpdate>(push.Payload, Json);
        return update!.Items.Any(i => i.CategoryCode == category && i.From <= day && day <= i.To && i.Available == available);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition) => await WaitForAsync(() => Task.FromResult(condition()));

    /// <summary>Polls <paramref name="condition"/> until it holds or <see cref="Patience"/> passes (the pump works in the background).</summary>
    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100, Ct);
        }

        return await condition();
    }

    private Task<T> ManualAsync<T>(Func<ChannelManualEntryService, Task<T>> call) => ChannelTestWorld.RunAsync(host, call);

    private async Task<Guid> RestrictAsync(TestChannel channel, RestrictionRequest request)
    {
        var added = await ChannelTestWorld.RunAsync<ChannelRestrictionService, Result<Guid>>(host, s => s.AddAsync(channel.Id, request, Ct));
        added.IsSuccess.ShouldBeTrue(added.IsFailure ? added.Error.ToString() : null);
        return added.Value;
    }

    private async Task<IReadOnlyList<SyncItemEntry>> ItemsAsync(TestChannel channel) =>
        (await ChannelTestWorld.RunAsync<ChannelMonitoringService, Result<PagedResult<SyncItemEntry>>>(host, s => s.ListSyncItemsAsync(SyncQueueFilter.All, channel.Id, new PageRequest(1, 100), Ct))).Value.Items;

    private async Task<Guid> InsertFailedItemAsync(TestChannel channel)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var item = ChannelSyncItem.Enqueue(channel.Id, channel.Agency.AgencyId, ChannelSyncOperation.PushRates, null, "rates", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        item.Failed("channels.sync.push_failed: simulated outage", null, DateTimeOffset.UtcNow, maxAttempts: 1);
        db.Set<ChannelSyncItem>().Add(item);
        await db.SaveChangesAsync(Ct);
        (await db.Set<ChannelSyncItem>().CountAsync(i => i.Id == item.Id && i.Status == ChannelSyncStatus.Failed, Ct)).ShouldBe(1);
        return item.Id;
    }
}
