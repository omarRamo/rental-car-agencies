using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Channels;

/// <summary>
/// An update to send to a connected channel (docs/conception/phase-5-canaux.md §3 "Sortant"): availability of a category
/// (or of every mapped one), prices, or a full resynchronization. Triggers within the debounce window coalesce into one
/// pending item per <see cref="CoalesceKey"/>. The payload is built from fresh data at each attempt and kept (canonical
/// JSON, no personal data); a failure is retried with growing delays (<see cref="ChannelSyncBackoff"/>) until
/// <c>Channels:Sync:MaxAttempts</c>, then the item fails for good (health entry <c>channels</c> degraded) and waits
/// for a replay from the screen. It carries the agency of its channel.
/// </summary>
public sealed class ChannelSyncItem : AggregateRoot, IAgencyOwned
{
    public const int CoalesceKeyMaxLength = 100;
    public const int ErrorMaxLength = 1000;
    public const int ReasonMaxLength = 40;

    private ChannelSyncItem()
    {
    }

    private ChannelSyncItem(Guid channelId, Guid agencyId, ChannelSyncOperation operation, Guid? categoryId, string reason, DateTimeOffset dueAt, DateTimeOffset now)
        : base(NewId())
    {
        ChannelId = channelId;
        AgencyId = agencyId;
        Operation = operation;
        CategoryId = categoryId;
        CoalesceKey = KeyOf(channelId, operation, categoryId);
        Reason = reason.Length > ReasonMaxLength ? reason[..ReasonMaxLength] : reason;
        Status = ChannelSyncStatus.Pending;
        CreatedAtUtc = now.ToUniversalTime();
        NextAttemptAtUtc = dueAt.ToUniversalTime();
    }

    public Guid ChannelId { get; private set; }

    /// <summary>The agency of the channel.</summary>
    public Guid AgencyId { get; private set; }

    public ChannelSyncOperation Operation { get; private set; }

    /// <summary>The category of a <see cref="ChannelSyncOperation.PushAvailability"/>; <see langword="null"/>: every mapped category.</summary>
    public Guid? CategoryId { get; private set; }

    /// <summary>Channel, operation and category: one pending item per key at a time (debounce).</summary>
    public string CoalesceKey { get; private set; } = null!;

    /// <summary>What triggered the item (<c>availability</c>, <c>rates</c>, <c>restriction</c>, <c>mapping</c>, <c>nightly</c>…).</summary>
    public string Reason { get; private set; } = null!;

    public ChannelSyncStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>When the item is due (debounce, then the next retry).</summary>
    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    public DateTimeOffset? LastAttemptAtUtc { get; private set; }

    public DateTimeOffset? SentAtUtc { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>The canonical message of the last attempt (JSON, no personal data).</summary>
    public string? Payload { get; private set; }

    /// <summary>The run that took the item (several runs never send the same item at once).</summary>
    public Guid? ClaimToken { get; private set; }

    public DateTimeOffset? ClaimedUntilUtc { get; private set; }

    public static string KeyOf(Guid channelId, ChannelSyncOperation operation, Guid? categoryId) =>
        string.Create(CultureInfo.InvariantCulture, $"{channelId:N}:{(int)operation}:{(categoryId is { } id ? id.ToString("N") : "all")}");

    /// <summary>A new pending item of the channel <paramref name="channelId"/> of <paramref name="agencyId"/>, due at <paramref name="dueAt"/> (now plus the debounce window).</summary>
    public static ChannelSyncItem Enqueue(Guid channelId, Guid agencyId, ChannelSyncOperation operation, Guid? categoryId, string reason, DateTimeOffset dueAt, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (channelId == Guid.Empty || agencyId == Guid.Empty || !Enum.IsDefined(operation) || (operation != ChannelSyncOperation.PushAvailability && categoryId is not null))
        {
            throw new ArgumentException("A synchronization item needs its channel and agency; only an availability push targets one category.", nameof(channelId));
        }

        return new ChannelSyncItem(channelId, agencyId, operation, categoryId, reason, dueAt, now);
    }

    /// <summary>Whether the item waits to be sent and is due at <paramref name="now"/>.</summary>
    public bool IsDue(DateTimeOffset now) => Status == ChannelSyncStatus.Pending && NextAttemptAtUtc <= now && !IsClaimed(now);

    public bool IsClaimed(DateTimeOffset now) => ClaimedUntilUtc is { } until && until > now;

    /// <summary>A new trigger of the same key: the pending item is sent no later than <paramref name="dueAt"/>.</summary>
    public void Coalesce(DateTimeOffset dueAt)
    {
        if (Status == ChannelSyncStatus.Pending && Attempts == 0 && dueAt < NextAttemptAtUtc)
        {
            NextAttemptAtUtc = dueAt.ToUniversalTime();
        }
    }

    /// <summary>Takes the item for one run until <paramref name="until"/>; another run cannot take it meanwhile.</summary>
    public bool TryClaim(Guid token, DateTimeOffset now, DateTimeOffset until)
    {
        if (!IsDue(now))
        {
            return false;
        }

        ClaimToken = token;
        ClaimedUntilUtc = until.ToUniversalTime();
        return true;
    }

    /// <summary>The channel accepted the update.</summary>
    public void Succeeded(string? payload, DateTimeOffset at)
    {
        Attempts++;
        Status = ChannelSyncStatus.Sent;
        LastAttemptAtUtc = at.ToUniversalTime();
        SentAtUtc = LastAttemptAtUtc;
        LastError = null;
        Payload = payload;
        Release();
    }

    /// <summary>
    /// The attempt failed: retried after <see cref="ChannelSyncBackoff.DelayAfter"/>, or failed for good once
    /// <paramref name="maxAttempts"/> attempts are spent.
    /// </summary>
    public void Failed(string error, string? payload, DateTimeOffset at, int maxAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        Attempts++;
        LastAttemptAtUtc = at.ToUniversalTime();
        LastError = error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
        Payload = payload ?? Payload;
        if (Attempts >= maxAttempts)
        {
            Status = ChannelSyncStatus.Failed;
        }
        else
        {
            NextAttemptAtUtc = LastAttemptAtUtc.Value + ChannelSyncBackoff.DelayAfter(Attempts);
        }

        Release();
    }

    /// <summary>A newer pending item of the same key was sent in the same run: this one is done too.</summary>
    public void SentWith(ChannelSyncItem sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        Status = ChannelSyncStatus.Sent;
        LastAttemptAtUtc = sent.LastAttemptAtUtc;
        SentAtUtc = sent.SentAtUtc;
        LastError = null;
        Payload = sent.Payload;
        Release();
    }

    /// <summary>Gives up the item without sending it (<paramref name="reason"/>: the channel was disconnected, a twin carries the update).</summary>
    public void Skip(string reason, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Status = ChannelSyncStatus.Skipped;
        LastAttemptAtUtc = at.ToUniversalTime();
        LastError = reason.Length > ErrorMaxLength ? reason[..ErrorMaxLength] : reason;
        Release();
    }

    /// <summary>Sends the item again now, with fresh data and every attempt available again (replay from the screen).</summary>
    public void Replay(DateTimeOffset at)
    {
        Status = ChannelSyncStatus.Pending;
        Attempts = 0;
        NextAttemptAtUtc = at.ToUniversalTime();
        LastError = null;
        Release();
    }

    /// <summary>Gives the item back without an attempt (the run stopped before sending it).</summary>
    public void Release()
    {
        ClaimToken = null;
        ClaimedUntilUtc = null;
    }
}

/// <summary>Growing delays between the attempts of an outbound update: 30 s, 2 min, 5 min, 15 min, 1 h, 3 h, then 6 h.</summary>
public static class ChannelSyncBackoff
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(3),
        TimeSpan.FromHours(6),
    ];

    /// <summary>The delay before the attempt that follows the <paramref name="failedAttempts"/>-th failure.</summary>
    public static TimeSpan DelayAfter(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);
        return Delays[Math.Min(failedAttempts, Delays.Length) - 1];
    }
}
