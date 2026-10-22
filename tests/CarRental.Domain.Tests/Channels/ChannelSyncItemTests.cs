using CarRental.Domain.Channels;
using static CarRental.Domain.Tests.Channels.ChannelTestData;

namespace CarRental.Domain.Tests.Channels;

/// <summary>Outbound items: debounce by key, claims, retries with growing delays, failure for good and replay.</summary>
public sealed class ChannelSyncItemTests
{
    private static readonly Guid ChannelId = Guid.CreateVersion7();
    private static readonly Guid CategoryId = Guid.CreateVersion7();

    [Fact]
    public void An_item_is_pending_until_its_debounce_window_has_passed()
    {
        var item = ChannelSyncItem.Enqueue(ChannelId, AgencyId, ChannelSyncOperation.PushAvailability, CategoryId, "availability", Now.AddSeconds(10), Now);

        item.Status.ShouldBe(ChannelSyncStatus.Pending);
        item.IsDue(Now).ShouldBeFalse();
        item.IsDue(Now.AddSeconds(10)).ShouldBeTrue();
        item.CoalesceKey.ShouldBe(ChannelSyncItem.KeyOf(ChannelId, ChannelSyncOperation.PushAvailability, CategoryId));
        ChannelSyncItem.KeyOf(ChannelId, ChannelSyncOperation.PushAvailability, null).ShouldEndWith(":all");
    }

    [Fact]
    public void Only_an_availability_push_targets_a_category()
    {
        Should.Throw<ArgumentException>(() => ChannelSyncItem.Enqueue(ChannelId, AgencyId, ChannelSyncOperation.PushRates, CategoryId, "rates", Now, Now));
        Should.Throw<ArgumentException>(() => ChannelSyncItem.Enqueue(Guid.Empty, AgencyId, ChannelSyncOperation.PushRates, null, "rates", Now, Now));
        Should.Throw<ArgumentException>(() => ChannelSyncItem.Enqueue(ChannelId, Guid.Empty, ChannelSyncOperation.PushRates, null, "rates", Now, Now), "an item carries the agency of its channel");
        ChannelSyncItem.Enqueue(ChannelId, AgencyId, ChannelSyncOperation.PushRates, null, "rates", Now, Now).AgencyId.ShouldBe(AgencyId);
    }

    [Fact]
    public void A_new_trigger_of_the_same_key_never_postpones_the_pending_item()
    {
        var item = ChannelSyncItem.Enqueue(ChannelId, AgencyId, ChannelSyncOperation.PushRates, null, "rates", Now.AddSeconds(10), Now);

        item.Coalesce(Now.AddSeconds(30));
        item.NextAttemptAtUtc.ShouldBe(Now.AddSeconds(10));
        item.Coalesce(Now.AddSeconds(2));
        item.NextAttemptAtUtc.ShouldBe(Now.AddSeconds(2));
    }

    [Fact]
    public void A_claimed_item_is_not_taken_by_another_run_until_its_claim_expires()
    {
        var item = ChannelSyncItem.Enqueue(ChannelId, AgencyId, ChannelSyncOperation.FullResync, null, "nightly", Now, Now);

        item.TryClaim(Guid.CreateVersion7(), Now, Now.AddMinutes(5)).ShouldBeTrue();
        item.TryClaim(Guid.CreateVersion7(), Now.AddMinutes(1), Now.AddMinutes(6)).ShouldBeFalse();
        item.TryClaim(Guid.CreateVersion7(), Now.AddMinutes(5), Now.AddMinutes(10)).ShouldBeTrue("an abandoned claim expires");
        item.Release();
        item.ClaimToken.ShouldBeNull();
    }

    [Fact]
    public void Failures_are_retried_with_growing_delays_then_fail_for_good()
    {
        var item = ChannelSyncItem.Enqueue(ChannelId, AgencyId, ChannelSyncOperation.PushRates, null, "rates", Now, Now);
        var at = Now;
        var delays = new List<TimeSpan>();
        for (var attempt = 1; attempt < 4; attempt++)
        {
            item.Failed("channels.sync.push_failed: 503", "{}", at, maxAttempts: 4);
            delays.Add(item.NextAttemptAtUtc - at);
            item.Status.ShouldBe(ChannelSyncStatus.Pending);
            at = item.NextAttemptAtUtc;
        }

        item.Failed("channels.sync.push_failed: 503", null, at, maxAttempts: 4);

        delays.ShouldBe([TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)]);
        item.Status.ShouldBe(ChannelSyncStatus.Failed);
        item.Attempts.ShouldBe(4);
        item.Payload.ShouldBe("{}", "the last payload is kept");
        item.IsDue(at.AddDays(1)).ShouldBeFalse("only a replay sends it again");
        ChannelSyncBackoff.DelayAfter(50).ShouldBe(TimeSpan.FromHours(6));
    }

    [Fact]
    public void A_replay_sends_again_now_with_every_attempt_available()
    {
        var item = ChannelSyncItem.Enqueue(ChannelId, AgencyId, ChannelSyncOperation.PushRates, null, "rates", Now, Now);
        item.Failed("boom", null, Now, maxAttempts: 1);

        item.Replay(Now.AddHours(1));

        item.Status.ShouldBe(ChannelSyncStatus.Pending);
        item.Attempts.ShouldBe(0);
        item.LastError.ShouldBeNull();
        item.IsDue(Now.AddHours(1)).ShouldBeTrue();
    }

    [Fact]
    public void A_success_records_the_payload_sent_and_items_of_the_same_key_follow_it()
    {
        var first = ChannelSyncItem.Enqueue(ChannelId, AgencyId, ChannelSyncOperation.PushAvailability, CategoryId, "availability", Now, Now);
        var twin = ChannelSyncItem.Enqueue(ChannelId, AgencyId, ChannelSyncOperation.PushAvailability, CategoryId, "restriction", Now, Now);

        first.Succeeded("{\"items\":[]}", Now.AddSeconds(1));
        twin.SentWith(first);

        first.Status.ShouldBe(ChannelSyncStatus.Sent);
        first.SentAtUtc.ShouldBe(Now.AddSeconds(1));
        first.Attempts.ShouldBe(1);
        twin.Status.ShouldBe(ChannelSyncStatus.Sent);
        twin.Payload.ShouldBe(first.Payload);
    }
}
