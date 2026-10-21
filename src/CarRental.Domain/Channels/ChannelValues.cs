using CarRental.Domain.Common;

namespace CarRental.Domain.Channels;

/// <summary>When (UTC) and by whom a change of the Channels module is made; rules on dates use the Tunis day of <see cref="At"/>.</summary>
public sealed record ChannelStamp
{
    public ChannelStamp(DateTimeOffset at, string? by)
    {
        At = at.ToUniversalTime();
        var name = by?.Trim();
        By = string.IsNullOrEmpty(name)
            ? null
            : name.Length > AuditActor.UserNameMaxLength ? name[..AuditActor.UserNameMaxLength] : name;
    }

    /// <summary>Instant of the change, UTC.</summary>
    public DateTimeOffset At { get; }

    /// <summary>User name of the author, or a system actor such as <c>system:channel:booking-com</c>.</summary>
    public string? By { get; }

    /// <summary>The Tunis calendar date of the change.</summary>
    public DateOnly Today => TunisTime.DateOf(At);
}

/// <summary>Rules on the codes and references exchanged with channels.</summary>
public static class ChannelCodes
{
    /// <summary>Longest code of a channel (its address segment, e.g. <c>booking-com</c>).</summary>
    public const int ChannelCodeMaxLength = 32;

    public const int ChannelCodeMinLength = 2;

    /// <summary>Longest code of the channel side of a mapping (vehicle group, depot or OpenTravel code).</summary>
    public const int MappingCodeMaxLength = 32;

    /// <summary>Longest internal extra code (same limit as the rate plan catalogue).</summary>
    public const int InternalCodeMaxLength = 32;

    /// <summary>Longest reference of a booking in its channel (same limit as the rental file).</summary>
    public const int ReferenceMaxLength = 64;

    /// <summary>
    /// The code of a channel, lower-cased: 2 to 32 characters among <c>a-z</c>, <c>0-9</c> and inner dashes. It is the
    /// address segment of the inbound endpoint (<c>/api/channels/{code}/…</c>), unique on the platform.
    /// </summary>
    public static Result<string> ChannelCode(string? code)
    {
        var normalized = (code ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length is < ChannelCodeMinLength or > ChannelCodeMaxLength
            || normalized[0] == '-'
            || normalized[^1] == '-'
            || !normalized.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
        {
            return ChannelsErrors.CodeInvalid;
        }

        return normalized;
    }

    /// <summary>
    /// The channel side of a mapping, kept as typed but trimmed: 1 to 32 visible characters among letters, digits,
    /// <c>.</c>, <c>_</c>, <c>-</c> and <c>/</c> (no list is invented: the agency types the codes of its channel).
    /// </summary>
    public static Result<string> MappingCode(string? code)
    {
        var trimmed = (code ?? string.Empty).Trim();
        return trimmed.Length is 0 or > MappingCodeMaxLength || !trimmed.All(IsCodeCharacter)
            ? ChannelsErrors.MappingCodeInvalid
            : trimmed;
    }

    /// <summary>An extra code of the rate plan catalogue (upper-cased): letters, digits and underscores, starting with a letter.</summary>
    public static Result<string> InternalCode(string? code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length is < 2 or > InternalCodeMaxLength
            || !char.IsAsciiLetterUpper(normalized[0])
            || !normalized.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            return ChannelsErrors.MappingInternalCodeInvalid;
        }

        return normalized;
    }

    /// <summary>
    /// The reference of a booking in its channel, trimmed: 1 to 64 characters among letters, digits, <c>.</c>,
    /// <c>_</c>, <c>-</c> and <c>/</c>. It is the idempotency key of the channel's messages.
    /// </summary>
    public static Result<string> Reference(string? reference)
    {
        var trimmed = (reference ?? string.Empty).Trim();
        return trimmed.Length is 0 or > ReferenceMaxLength || !trimmed.All(IsCodeCharacter)
            ? ChannelsErrors.ReferenceInvalid
            : trimmed;
    }

    /// <summary>Whether two codes of a channel are the same (case ignored: channels mix cases).</summary>
    public static bool SameCode(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsCodeCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/';
}
