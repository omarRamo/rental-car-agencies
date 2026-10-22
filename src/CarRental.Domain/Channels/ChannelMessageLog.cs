using System.Text;
using CarRental.Domain.Common;

namespace CarRental.Domain.Channels;

/// <summary>
/// A message exchanged with a channel (SPEC §10 "journal de chaque message entrant et sortant"): direction, operation,
/// channel reference, outcome, duration, an excerpt WITHOUT personal data (built from the canonical message: codes,
/// dates, amounts, never names, phones or e-mails) and the error code. A record of the agency of the channel, or of the
/// platform itself when the address named no channel open to the outside (unknown code, module off, agency suspended).
/// Appended only; purged after the retention of <c>channels.message_log_retention_days</c>.
/// </summary>
public sealed class ChannelMessageLog : Entity, IAgencyOrPlatform
{
    public const int ChannelCodeMaxLength = ChannelCodes.ChannelCodeMaxLength;
    public const int ExcerptMaxLength = 1000;
    public const int ErrorCodeMaxLength = 100;
    public const int RemoteAddressMaxLength = AuditActor.IpAddressMaxLength;
    public const int CorrelationIdMaxLength = AuditActor.CorrelationIdMaxLength;

    private ChannelMessageLog()
    {
    }

    private ChannelMessageLog(ChannelMessageRecord record)
        : base(NewId())
    {
        ChannelId = record.ChannelId;
        AgencyId = record.AgencyId;
        ChannelCode = Clip(record.ChannelCode, ChannelCodeMaxLength) ?? "?";
        Direction = record.Direction;
        Operation = record.Operation;
        ChannelReference = Clip(record.ChannelReference, ChannelCodes.ReferenceMaxLength);
        Status = record.Status;
        HttpStatus = record.HttpStatus;
        DurationMilliseconds = (int)Math.Clamp(Math.Round(record.Duration.TotalMilliseconds), 0, int.MaxValue);
        Excerpt = Clip(record.Excerpt, ExcerptMaxLength);
        ErrorCode = Clip(record.ErrorCode, ErrorCodeMaxLength);
        RemoteAddress = Clip(record.RemoteAddress, RemoteAddressMaxLength);
        CorrelationId = Clip(record.CorrelationId, CorrelationIdMaxLength);
        BookingId = record.BookingId;
        OccurredAtUtc = record.OccurredAtUtc.ToUniversalTime();
    }

    /// <summary>The channel, or <see langword="null"/> when the address named an unknown channel.</summary>
    public Guid? ChannelId { get; private set; }

    /// <summary>The agency of the channel; <see langword="null"/> for a message to no known channel (a platform record).</summary>
    public Guid? AgencyId { get; private set; }

    /// <summary>The code of the channel as addressed (kept for unknown channels too).</summary>
    public string ChannelCode { get; private set; } = null!;

    public ChannelMessageDirection Direction { get; private set; }

    public ChannelMessageOperation Operation { get; private set; }

    public string? ChannelReference { get; private set; }

    public ChannelMessageStatus Status { get; private set; }

    /// <summary>HTTP status answered (inbound) or received (outbound), when there was one.</summary>
    public int? HttpStatus { get; private set; }

    public int DurationMilliseconds { get; private set; }

    /// <summary>What the message carried, without personal data (codes, dates, amounts).</summary>
    public string? Excerpt { get; private set; }

    /// <summary>The stable error code of a refusal or failure (e.g. <c>availability.not_available</c>).</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>The address the channel called from (inbound).</summary>
    public string? RemoteAddress { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>The rental file concerned, when known.</summary>
    public Guid? BookingId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>A log row; values that are too long are clipped and control characters removed (a log never fails).</summary>
    public static ChannelMessageLog Record(ChannelMessageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new ChannelMessageLog(record);
    }

    private static string? Clip(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var character in value.Trim())
        {
            if (builder.Length == maxLength)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString();
    }
}

/// <summary>
/// What a <see cref="ChannelMessageLog"/> row records: the channel and its agency (both <see langword="null"/> for a
/// message to no known channel), its code as addressed, direction, operation, outcome, duration and time.
/// </summary>
public sealed record ChannelMessageRecord(
    Guid? ChannelId,
    Guid? AgencyId,
    string? ChannelCode,
    ChannelMessageDirection Direction,
    ChannelMessageOperation Operation,
    ChannelMessageStatus Status,
    TimeSpan Duration,
    DateTimeOffset OccurredAtUtc)
{
    public string? ChannelReference { get; init; }

    public int? HttpStatus { get; init; }

    public string? Excerpt { get; init; }

    public string? ErrorCode { get; init; }

    public string? RemoteAddress { get; init; }

    public string? CorrelationId { get; init; }

    public Guid? BookingId { get; init; }
}
