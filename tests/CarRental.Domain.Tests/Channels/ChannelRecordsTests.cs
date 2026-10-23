using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Channels.ChannelTestData;

namespace CarRental.Domain.Tests.Channels;

/// <summary>Extranet reminders, payouts, the synchronization state, the reconciliation, the message log and the codes.</summary>
public sealed class ChannelRecordsTests
{
    [Fact]
    public void A_restriction_change_becomes_a_reminder_until_acknowledged()
    {
        var quota = ChannelQuota.Reserve(NewChannel(), Guid.CreateVersion7(), new DateOnly(2027, 7, 1), new DateOnly(2027, 7, 31), 2, null, Stamp).Value;

        var reminder = ChannelReplicationItem.ForRestriction(quota, ChannelReplicationKind.QuotaAdded, Stamp);

        reminder.QuotaId.ShouldBe(quota.Id);
        reminder.AgencyId.ShouldBe(AgencyId, "the agency of the channel of the quota");
        reminder.Vehicles.ShouldBe(2);
        reminder.StartDate.ShouldBe(quota.StartDate);
        reminder.IsAcknowledged.ShouldBeFalse();
        reminder.Acknowledge(new ChannelStamp(Now.AddHours(1), "agent@demo.local")).ShouldBeTrue();
        reminder.Acknowledge(Stamp).ShouldBeFalse();
        reminder.AcknowledgedBy.ShouldBe("agent@demo.local");
        Should.Throw<ArgumentOutOfRangeException>(() => ChannelReplicationItem.ForRestriction(quota, ChannelReplicationKind.RatesPublished, Stamp));
        var rates = ChannelReplicationItem.ForRates(quota.ChannelId, AgencyId, "Grille 2027 v3", Stamp);
        rates.Detail.ShouldBe("Grille 2027 v3");
        rates.AgencyId.ShouldBe(AgencyId);
        Should.Throw<ArgumentException>(() => ChannelReplicationItem.ForRates(quota.ChannelId, Guid.Empty, null, Stamp));
    }

    [Fact]
    public void A_payout_is_a_positive_tnd_amount_of_a_month()
    {
        var channel = NewChannel();

        var payout = ChannelPayout.Record(channel, 2027, 7, Tnd(12_500.5m), " VIR-77 ", null, Stamp).Value;
        ChannelPayout.Record(channel, 2027, 13, Tnd(1m), null, null, Stamp).Error.ShouldBe(ChannelsErrors.PayoutMonthInvalid);
        ChannelPayout.Record(channel, 2027, 7, Tnd(-1m), null, null, Stamp).Error.ShouldBe(ChannelsErrors.PayoutAmountInvalid);
        ChannelPayout.Record(channel, 2027, 7, Money.Of(1m, Currency.Eur), null, null, Stamp).Error.ShouldBe(ChannelsErrors.PayoutAmountInvalid);
        payout.Change(Tnd(1m), new string('r', ChannelPayout.ReferenceMaxLength + 1), null, Stamp).Error.ShouldBe(ChannelsErrors.PayoutTextTooLong);

        payout.Reference.ShouldBe("VIR-77");
        payout.Amount.ShouldBe(Tnd(12_500.5m));
        payout.ChannelId.ShouldBe(channel.Id);
        payout.AgencyId.ShouldBe(channel.AgencyId);
    }

    [Fact]
    public void The_price_fingerprint_reports_a_change_but_not_the_first_observation()
    {
        var state = ChannelSyncState.For(Guid.CreateVersion7(), AgencyId);

        state.ObserveRates("plan:1", Now).ShouldBeFalse();
        state.ObserveRates("plan:1", Now).ShouldBeFalse();
        state.ObserveRates("plan:2", Now).ShouldBeTrue();
        state.RatesFingerprint.ShouldBe("plan:2");
        state.AgencyId.ShouldBe(AgencyId);
        Should.Throw<ArgumentException>(() => ChannelSyncState.For(Guid.CreateVersion7(), Guid.Empty));
    }

    [Fact]
    public void The_reconciliation_compares_the_payout_with_the_expected_settlement()
    {
        ChannelBookingFigures[] bookings =
        [
            new(ChannelBookingState.Active, ChannelPaymentMode.PrepaidToChannel, Tnd(500m), Tnd(75m), Tnd(425m)),
            new(ChannelBookingState.Active, ChannelPaymentMode.PrepaidToChannel, Tnd(300m), Tnd(45m), Tnd(255m)),
            new(ChannelBookingState.Active, ChannelPaymentMode.PayOnSite, Tnd(200m), Tnd(30m), Tnd(170m)),
            new(ChannelBookingState.Cancelled, ChannelPaymentMode.PrepaidToChannel, Tnd(400m), Money.Zero, Tnd(400m)),
            new(ChannelBookingState.NoShow, ChannelPaymentMode.PayOnSite, Tnd(100m), Tnd(15m), Tnd(85m)),
        ];

        var figures = ChannelReconciliation.Compute(bookings, Tnd(630m));

        figures.Bookings.ShouldBe(4);
        figures.Cancelled.ShouldBe(1);
        figures.Gross.ShouldBe(Tnd(1100m));
        figures.Commission.ShouldBe(Tnd(165m));
        figures.Net.ShouldBe(Tnd(935m));
        figures.PrepaidNet.ShouldBe(Tnd(680m));
        figures.PayOnSiteCommission.ShouldBe(Tnd(45m));
        figures.ExpectedSettlement.ShouldBe(Tnd(635m));
        figures.Difference.ShouldBe(Tnd(-5m));
        ChannelReconciliation.Compute([], null).Difference.ShouldBeNull();
    }

    [Fact]
    public void A_log_row_clips_long_values_and_removes_control_characters()
    {
        var row = ChannelMessageLog.Record(new ChannelMessageRecord(
            null,
            null,
            new string('c', 50),
            ChannelMessageDirection.Inbound,
            ChannelMessageOperation.Unknown,
            ChannelMessageStatus.Rejected,
            TimeSpan.FromMilliseconds(12.6),
            Now)
        {
            Excerpt = "line1\nline2" + new string('x', 2000),
            ErrorCode = "channels.inbound.unknown_channel",
        });

        row.ChannelCode.Length.ShouldBe(ChannelMessageLog.ChannelCodeMaxLength);
        row.Excerpt!.Length.ShouldBe(ChannelMessageLog.ExcerptMaxLength);
        row.Excerpt.ShouldStartWith("line1 line2");
        row.DurationMilliseconds.ShouldBe(13);
        row.ChannelReference.ShouldBeNull();
        row.AgencyId.ShouldBeNull("a message to no known channel is a record of the platform");
    }

    [Theory]
    [InlineData("BDC-4711", true)]
    [InlineData(" 1234567890 ", true)]
    [InlineData("A/B_C.D", true)]
    [InlineData("", false)]
    [InlineData("with space", false)]
    [InlineData("é", false)]
    public void A_channel_reference_is_a_short_code(string reference, bool valid)
    {
        ChannelCodes.Reference(reference).IsSuccess.ShouldBe(valid);
        ChannelCodes.Reference(new string('1', ChannelCodes.ReferenceMaxLength + 1)).IsFailure.ShouldBeTrue();
    }
}
