using CarRental.Domain.Common;

namespace CarRental.Domain.Channels;

/// <summary>
/// A change to replicate by hand on the extranet of a channel sold manually (SPEC §10 "le planning signale les arrêts
/// de vente à reporter sur l'extranet"): a quota or stop-sell added, changed or removed, or new prices published. It
/// stays on the list until someone acknowledges it.
/// </summary>
public sealed class ChannelReplicationItem : Entity
{
    public const int DetailMaxLength = 100;

    private ChannelReplicationItem()
    {
    }

    private ChannelReplicationItem(Guid channelId, ChannelReplicationKind kind, ChannelStamp stamp)
        : base(NewId())
    {
        ChannelId = channelId;
        Kind = kind;
        CreatedAtUtc = stamp.At;
        CreatedBy = stamp.By;
    }

    public Guid ChannelId { get; private set; }

    public ChannelReplicationKind Kind { get; private set; }

    /// <summary>The quota or stop-sell concerned, if any.</summary>
    public Guid? QuotaId { get; private set; }

    public Guid? CategoryId { get; private set; }

    public Guid? LocationId { get; private set; }

    public DateOnly? StartDate { get; private set; }

    public DateOnly? EndDate { get; private set; }

    /// <summary>Vehicles kept for the other channels (a quota).</summary>
    public int? Vehicles { get; private set; }

    /// <summary>What was published (e.g. the plan name and version), for a price change.</summary>
    public string? Detail { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public string? CreatedBy { get; private set; }

    public DateTimeOffset? AcknowledgedAtUtc { get; private set; }

    public string? AcknowledgedBy { get; private set; }

    public bool IsAcknowledged => AcknowledgedAtUtc is not null;

    /// <summary>A quota or stop-sell to replicate (its state after the change; the removed one for a removal).</summary>
    public static ChannelReplicationItem ForRestriction(ChannelQuota quota, ChannelReplicationKind kind, ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentNullException.ThrowIfNull(stamp);
        if (kind is ChannelReplicationKind.RatesPublished || !Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A restriction change is a quota or stop-sell change.");
        }

        return new ChannelReplicationItem(quota.ChannelId, kind, stamp)
        {
            QuotaId = quota.Id,
            CategoryId = quota.CategoryId,
            LocationId = quota.LocationId,
            StartDate = quota.StartDate,
            EndDate = quota.EndDate,
            Vehicles = quota.Kind == ChannelQuotaKind.Reserve ? quota.ReservedVehicles : null,
        };
    }

    /// <summary>New prices to replicate (<paramref name="detail"/>: what was published).</summary>
    public static ChannelReplicationItem ForRates(Guid channelId, string? detail, ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var text = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
        return new ChannelReplicationItem(channelId, ChannelReplicationKind.RatesPublished, stamp)
        {
            Detail = text?.Length > DetailMaxLength ? text[..DetailMaxLength] : text,
        };
    }

    /// <summary>Marks the change as replicated; <see langword="false"/> when it already was.</summary>
    public bool Acknowledge(ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        if (IsAcknowledged)
        {
            return false;
        }

        AcknowledgedAtUtc = stamp.At;
        AcknowledgedBy = stamp.By;
        return true;
    }
}

/// <summary>
/// The total paid by a channel for a month (reconciliation, SPEC §8 "versements Booking.com contre réservations et
/// commissions"), entered by hand. TODO(spec): the statement format of the channel, to import it instead.
/// </summary>
public sealed class ChannelPayout : AggregateRoot
{
    public const int ReferenceMaxLength = 64;
    public const int NoteMaxLength = 300;

    private ChannelPayout()
    {
    }

    private ChannelPayout(Guid channelId, int year, int month)
        : base(NewId())
    {
        ChannelId = channelId;
        Year = year;
        Month = month;
    }

    public Guid ChannelId { get; private set; }

    public int Year { get; private set; }

    public int Month { get; private set; }

    /// <summary>Money received from the channel for the month (net of its commissions as the channel computes them).</summary>
    public Money Amount { get; private set; }

    public string? Reference { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public string? RecordedBy { get; private set; }

    public static Result<ChannelPayout> Record(Guid channelId, int year, int month, Money amount, string? reference, string? note, ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        if (channelId == Guid.Empty)
        {
            throw new ArgumentException("A payout belongs to a channel.", nameof(channelId));
        }

        if (year is < 2000 or > 2100 || month is < 1 or > 12)
        {
            return ChannelsErrors.PayoutMonthInvalid;
        }

        var payout = new ChannelPayout(channelId, year, month);
        var changed = payout.Change(amount, reference, note, stamp);
        return changed.IsFailure ? changed.Error : payout;
    }

    public Result Change(Money amount, string? reference, string? note, ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        if (amount.Currency != Currency.Tnd || amount.IsNegative || amount.Amount > ChannelCommissionRule.MaxAmount * 100)
        {
            return ChannelsErrors.PayoutAmountInvalid;
        }

        var cleanReference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (cleanReference?.Length > ReferenceMaxLength || cleanNote?.Length > NoteMaxLength)
        {
            return ChannelsErrors.PayoutTextTooLong;
        }

        Amount = amount;
        Reference = cleanReference;
        Note = cleanNote;
        RecordedAtUtc = stamp.At;
        RecordedBy = stamp.By;
        Raise(new ChannelPayoutRecorded(Id, ChannelId, Year, Month));
        return Result.Success();
    }
}

/// <summary>
/// What the outbound synchronization of a channel last saw (one row per channel, <see cref="Entity.Id"/> = the channel):
/// the fingerprint of the published prices (a new plan or rule triggers a price push, or a reminder for a manual
/// channel) and the last full resynchronization.
/// </summary>
public sealed class ChannelSyncState : Entity
{
    public const int FingerprintMaxLength = 200;

    private ChannelSyncState()
    {
    }

    private ChannelSyncState(Guid channelId)
        : base(channelId)
    {
    }

    public string? RatesFingerprint { get; private set; }

    public DateTimeOffset? RatesCheckedAtUtc { get; private set; }

    public DateTimeOffset? LastFullResyncAtUtc { get; private set; }

    public static ChannelSyncState For(Guid channelId) => new(channelId);

    /// <summary>Records the fingerprint of the published prices; <see langword="true"/> when it changed (not on the first check).</summary>
    public bool ObserveRates(string fingerprint, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var value = fingerprint.Length > FingerprintMaxLength ? fingerprint[..FingerprintMaxLength] : fingerprint;
        var changed = RatesFingerprint is not null && !string.Equals(RatesFingerprint, value, StringComparison.Ordinal);
        RatesFingerprint = value;
        RatesCheckedAtUtc = at.ToUniversalTime();
        return changed;
    }

    public void FullResyncRequested(DateTimeOffset at) => LastFullResyncAtUtc = at.ToUniversalTime();
}

/// <summary>Figures of one channel booking for the reconciliation.</summary>
public sealed record ChannelBookingFigures(ChannelBookingState State, ChannelPaymentMode PaymentMode, Money Gross, Money Commission, Money Net);

/// <summary>
/// The figures of a channel for a month: bookings (cancelled apart), gross, commissions and net; what the channel should
/// pay (net of the prepaid bookings) and what it invoices (commissions of the bookings paid on site); the settlement
/// expected from the channel (the former minus the latter, as when the channel nets its commissions — TODO(spec): the
/// contract says how the channel settles), the payout entered and the difference.
/// </summary>
public sealed record ChannelMonthFigures(
    int Bookings,
    int Cancelled,
    Money Gross,
    Money Commission,
    Money Net,
    Money PrepaidNet,
    Money PayOnSiteCommission,
    Money ExpectedSettlement,
    Money? RecordedPayout)
{
    /// <summary>Payout entered minus expected settlement, once a payout is entered.</summary>
    public Money? Difference => RecordedPayout is { } payout ? payout - ExpectedSettlement : null;
}

/// <summary>Pure computation of the monthly reconciliation of a channel.</summary>
public static class ChannelReconciliation
{
    public static ChannelMonthFigures Compute(IEnumerable<ChannelBookingFigures> bookings, Money? recordedPayout)
    {
        ArgumentNullException.ThrowIfNull(bookings);
        var all = bookings.ToList();
        var counted = all.Where(b => b.State != ChannelBookingState.Cancelled).ToList();
        var gross = Sum(counted.Select(b => b.Gross));
        var commission = Sum(all.Select(b => b.Commission));
        var net = Sum(counted.Select(b => b.Net));
        var prepaidNet = Sum(counted.Where(b => b.PaymentMode == ChannelPaymentMode.PrepaidToChannel).Select(b => b.Net));
        var payOnSiteCommission = Sum(all.Where(b => b.PaymentMode == ChannelPaymentMode.PayOnSite).Select(b => b.Commission));
        return new ChannelMonthFigures(
            counted.Count,
            all.Count - counted.Count,
            gross,
            commission,
            net,
            prepaidNet,
            payOnSiteCommission,
            prepaidNet - payOnSiteCommission,
            recordedPayout);
    }

    private static Money Sum(IEnumerable<Money> amounts) => amounts.Aggregate(Money.Zero, (total, amount) => total + amount);
}
