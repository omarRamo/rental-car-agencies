using System.Net;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Channels.Administration;
using CarRental.Application.Channels.Outbound;
using CarRental.Application.Common.Paging;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.BackgroundJobs;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Support;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Bookings.BookingWorld;

namespace CarRental.Integration.Tests.Channels;

/// <summary>
/// The sales channels of each agency stay its own (ADR-0067), on SQL Server: the staff of an agency sees and changes its
/// channels only, a channel books into its own agency only, and an agency without its channels module (or suspended) has
/// channels that do not exist for the outside world, are not synchronized and whose screens are refused — its data kept.
/// </summary>
public sealed class ChannelTenancyTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_staff_of_an_agency_sees_and_changes_its_own_channels_only()
    {
        var (mine, theirs) = (await ChannelTestWorld.CreateAsync(host, Ct), await ChannelTestWorld.CreateAsync(host, Ct));
        var manager = TestActors.Staff(mine.Agency.AgencyId, Roles.Manager);

        await using var scope = host.CreateScope(manager);
        var channels = scope.ServiceProvider.GetRequiredService<ChannelAdministrationService>();
        var listed = (await channels.ListAsync(Ct)).Value;
        var foreign = await channels.GetAsync(theirs.Id, Ct);
        var renamed = await channels.UpdateSettingsAsync(theirs.Id, new UpdateChannelSettingsRequest("Canal détourné", ChannelState.Inactive, ChannelAdapterKind.Fake, ChannelPaymentMode.PayOnSite, 0.10m), Ct);
        var rotated = await channels.RotateApiKeyAsync(theirs.Id, Ct);
        var crossMapped = await channels.MapCategoryAsync(mine.Id, theirs.Agency.Ecmr, "ECMR", null, Ct);
        var createdThere = await channels.CreateAsync(
            new CreateChannelRequest(theirs.Agency.AgencyId, "t" + Guid.NewGuid().ToString("N")[..16], "Canal intrus", SalesChannel.BookingCom, ChannelAdapterKind.Fake, ChannelPaymentMode.PayOnSite, 0.15m),
            Ct);
        var sameCode = await channels.CreateAsync(
            new CreateChannelRequest(mine.Agency.AgencyId, theirs.Code, "Même code", SalesChannel.BookingCom, ChannelAdapterKind.Fake, ChannelPaymentMode.PayOnSite, 0.15m),
            Ct);
        var log = (await scope.ServiceProvider.GetRequiredService<ChannelMonitoringService>().ListMessagesAsync(new MessageLogFilter(ChannelId: theirs.Id), new PageRequest(1, 100), Ct)).Value;

        listed.ShouldContain(c => c.Id == mine.Id);
        listed.ShouldAllBe(c => c.AgencyId == mine.Agency.AgencyId);
        foreign.Error.ShouldBe(ChannelsErrors.NotFound);
        renamed.Error.ShouldBe(ChannelsErrors.NotFound);
        rotated.Error.ShouldBe(ChannelsErrors.NotFound);
        crossMapped.Error.ShouldBe(ChannelsErrors.MappingCategoryNotFound, "a channel maps the categories of its own agency only");
        createdThere.Error.ShouldBe(ChannelsErrors.AgencyNotFound, "a channel is created for the agency of the operation");
        sameCode.Error.ShouldBe(ChannelsErrors.CodeTaken, "the code is the address segment of the inbound endpoint, unique on the platform");
        log.Items.ShouldBeEmpty("the message log of another agency's channel is not shown");

        await using var check = host.CreateScope();
        var stored = await check.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Channel>().AsNoTracking().SingleAsync(c => c.Id == theirs.Id, Ct);
        stored.State.ShouldBe(ChannelState.Connected);
        stored.Name.ShouldNotBe("Canal détourné");
    }

    [Fact]
    public async Task A_channel_books_into_its_own_agency_and_never_reaches_the_bookings_of_another()
    {
        var (mine, theirs) = (await ChannelTestWorld.CreateAsync(host, Ct), await ChannelTestWorld.CreateAsync(host, Ct));
        using var http = host.CreateClient();
        var (ours, other) = (new FakeChannelClient(http, mine), new FakeChannelClient(http, theirs));
        var reference = "TEN-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

        var created = await ours.CreateAsync(FakeChannelClient.Reservation(reference, InDays(140), InDays(143), firstName: ChannelTestWorld.Name("Sami")), Ct);
        var read = await other.ReadAsync(reference, Ct);
        var cancelled = await other.CancelAsync(reference, Ct);
        var modified = await other.ModifyAsync(reference, FakeChannelClient.Reservation(reference, InDays(141), InDays(144)), Ct);

        // The same reference sent by the channel of the other agency never takes the booking over.
        var reused = await other.CreateAsync(FakeChannelClient.Reservation(reference, InDays(140), InDays(143), firstName: ChannelTestWorld.Name("Sami")), Ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await FakeChannelClient.JsonAsync(read, Ct)).GetProperty("error").GetString().ShouldBe(ChannelsErrors.BookingNotFound.Code);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        modified.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ((int)reused.StatusCode).ShouldNotBe(200, "never answered as a replay of the other agency's booking");

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var ourRecord = await db.Set<ChannelBooking>().AsNoTracking().SingleAsync(b => b.ChannelId == mine.Id && b.ChannelReference == reference, Ct);
        ourRecord.AgencyId.ShouldBe(mine.Agency.AgencyId);
        (await db.Set<ChannelBooking>().AnyAsync(b => b.ChannelId == theirs.Id && b.BookingId == ourRecord.BookingId, Ct)).ShouldBeFalse();
        var booking = (await RunAsync<IBookingsQueries, BookingSummaryInfo?>(host, Manager, q => q.GetSummaryAsync(ourRecord.BookingId, Ct))).ShouldNotBeNull();
        booking.AgencyId.ShouldBe(mine.Agency.AgencyId);
        booking.Status.ShouldBe(BookingStatusInfo.Confirmed, "untouched by the channel of the other agency");
        booking.PickupAt.ShouldBe(InDays(140));
    }

    [Fact]
    public async Task A_channel_of_an_agency_without_its_module_or_suspended_does_not_exist_for_the_outside_world()
    {
        var channel = await ChannelTestWorld.CreateAsync(host, Ct);
        using var http = host.CreateClient();
        var client = new FakeChannelClient(http, channel);
        var pickup = InDays(150);

        var open = await client.AvailabilityAsync(TestChannel.OfficeCode, pickup, pickup.AddDays(2), cancellationToken: Ct);
        await TestModules.DisableAsync(host.Factory.Services, channel.Agency.AgencyId, AgencyModules.Channels, Ct);
        var moduleOff = await client.AvailabilityAsync(TestChannel.OfficeCode, pickup, pickup.AddDays(2), cancellationToken: Ct);
        var booked = await client.CreateAsync(FakeChannelClient.Reservation("OFF-" + Guid.NewGuid().ToString("N")[..10], pickup, pickup.AddDays(2)), Ct);
        await TestModules.EnableAsync(host.Factory.Services, channel.Agency.AgencyId, AgencyModules.Channels, Ct);
        await SetActiveAsync(channel.Agency.AgencyId, active: false);
        var suspended = await client.AvailabilityAsync(TestChannel.OfficeCode, pickup, pickup.AddDays(2), cancellationToken: Ct);
        await SetActiveAsync(channel.Agency.AgencyId, active: true);
        var back = await client.AvailabilityAsync(TestChannel.OfficeCode, pickup, pickup.AddDays(2), cancellationToken: Ct);

        open.StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var closed in new[] { moduleOff, booked, suspended })
        {
            closed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await FakeChannelClient.JsonAsync(closed, Ct)).GetProperty("error").GetString().ShouldBe(ChannelsErrors.InboundUnknownChannel.Code, "the same answer as an unknown code");
        }

        back.StatusCode.ShouldBe(HttpStatusCode.OK, "the channel and its settings were kept");
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        (await db.Set<ChannelBooking>().AnyAsync(b => b.ChannelId == channel.Id, Ct)).ShouldBeFalse("nothing was booked while the channel was closed");
        (await db.Set<ChannelMessageLog>().CountAsync(m => m.ChannelId == channel.Id && m.Direction == ChannelMessageDirection.Inbound, Ct))
            .ShouldBe(2, "the requests to the closed channel are logged as to an unknown code, the platform's");
    }

    [Fact]
    public async Task The_synchronization_of_an_agency_without_its_module_waits_and_resumes_once_it_is_back()
    {
        var channel = await ChannelTestWorld.CreateAsync(host, Ct);
        await TestModules.DisableAsync(host.Factory.Services, channel.Agency.AgencyId, AgencyModules.Channels, Ct);

        // Due a little later: a run of the pump that saw the module still on (started just before) cannot take it.
        var due = DateTimeOffset.UtcNow.AddSeconds(5);
        var itemId = await QueueRatesUpdateAsync(channel, due);
        var fullResyncs = await CountAsync(channel, ChannelSyncOperation.FullResync);
        if (due - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(200) is { Ticks: > 0 } wait)
        {
            await Task.Delay(wait, Ct);
        }

        await RunSyncJobAsync();
        var resyncsQueued = await ResyncForAgencyAsync(channel.Agency.AgencyId);
        var waiting = await ItemAsync(itemId);
        var fullResyncsWhileOff = await CountAsync(channel, ChannelSyncOperation.FullResync);
        await TestModules.EnableAsync(host.Factory.Services, channel.Agency.AgencyId, AgencyModules.Channels, Ct);
        await RunSyncJobAsync();
        var sent = await WaitForAsync(() => ItemAsync(itemId), i => i.Status == ChannelSyncStatus.Sent, "the update sent once the module is back");

        waiting.Status.ShouldBe(ChannelSyncStatus.Pending, "nothing is sent to the channel of an agency without its module");
        waiting.Attempts.ShouldBe(0);
        resyncsQueued.ShouldBe(0, "the nightly resynchronization skips the agency");
        fullResyncsWhileOff.ShouldBe(fullResyncs);
        sent.AgencyId.ShouldBe(channel.Agency.AgencyId);
    }

    [Fact]
    public async Task The_channel_screens_are_refused_while_the_module_of_the_agency_is_off()
    {
        var agency = await CreateAsync(host, Ct);
        var (_, email) = await TestStaff.CreateAsync(host, Roles.Manager, agencyId: agency.AgencyId);
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, email, "/admin", Ct);
        var channels = new Uri("/admin/channels", UriKind.Relative);
        var log = new Uri("/admin/channels/log", UriKind.Relative);

        var refused = await client.GetAsync(channels, Ct);
        var logRefused = await client.GetAsync(log, Ct);
        await TestModules.EnableAsync(host.Factory.Services, agency.AgencyId, AgencyModules.Channels, Ct);
        var allowed = await client.GetAsync(channels, Ct);

        await ShouldBeDeniedAsync(refused);
        await ShouldBeDeniedAsync(logRefused);
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, "same session: the module applies at the next request");
    }

    private static async Task ShouldBeDeniedAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync(Ct));
        HttpSignIn.PathAndQuery(response.Headers.Location!).ShouldContain("/account/access-denied");
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what)
    {
        // Beyond the idle interval of the pump (30 seconds), should the job have been skipped.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, $"Timed out waiting for {what}.");
            await Task.Delay(100, Ct);
        }
    }

    /// <summary>A price update of the channel due at <paramref name="due"/>, queued like the dispatcher does.</summary>
    private async Task<Guid> QueueRatesUpdateAsync(TestChannel channel, DateTimeOffset due)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var item = ChannelSyncItem.Enqueue(channel.Id, channel.Agency.AgencyId, ChannelSyncOperation.PushRates, null, "rates", due, DateTimeOffset.UtcNow);
        db.Set<ChannelSyncItem>().Add(item);
        await db.SaveChangesAsync(Ct);
        return item.Id;
    }

    private async Task<ChannelSyncItem> ItemAsync(Guid itemId)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<ChannelSyncItem>().AsNoTracking().SingleAsync(i => i.Id == itemId, Ct);
    }

    private async Task<int> CountAsync(TestChannel channel, ChannelSyncOperation operation)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<ChannelSyncItem>().CountAsync(i => i.ChannelId == channel.Id && i.Operation == operation, Ct);
    }

    /// <summary>
    /// Runs the synchronization job as the scheduler does: the system across agencies, under the lock it shares with the
    /// in-process pump (a run that cannot take it is skipped, the pump then does the same work).
    /// </summary>
    private async Task RunSyncJobAsync()
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RecurringJobRunner>().RunAsync(ChannelSyncJob.JobId, Ct);
    }

    /// <summary>The nightly full resynchronization, run for one agency only (the others' channels are left alone).</summary>
    private async Task<int> ResyncForAgencyAsync(Guid agencyId)
    {
        await using var scope = host.CreateScope(TestActors.SystemFor(agencyId, "system:job:channels.full_resync"));
        return await scope.ServiceProvider.GetRequiredService<ChannelSyncDispatcher>().EnqueueFullResyncAsync("nightly", Ct);
    }

    /// <summary>Suspends the agency or lifts its suspension, as the support does.</summary>
    private async Task SetActiveAsync(Guid agencyId, bool active)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var agency = await db.Set<Agency>().SingleAsync(a => a.Id == agencyId, Ct);
        if (active)
        {
            agency.Activate();
        }
        else
        {
            agency.Deactivate();
        }

        await db.SaveChangesAsync(Ct);
    }
}
