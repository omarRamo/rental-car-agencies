using CarRental.Domain.Common;

namespace CarRental.Domain.Fleet;

/// <summary>
/// When (UTC) and by whom a fleet change is made. Rules that depend on "now" (status blocks, sale date, maintenance
/// start) use <see cref="At"/>; the vehicle history records both values.
/// </summary>
public sealed record ChangeStamp
{
    public ChangeStamp(DateTimeOffset at, string? by)
    {
        At = at.ToUniversalTime();
        var name = by?.Trim();
        By = string.IsNullOrEmpty(name)
            ? null
            : name.Length > AuditActor.UserNameMaxLength ? name[..AuditActor.UserNameMaxLength] : name;
    }

    /// <summary>Instant of the change, UTC.</summary>
    public DateTimeOffset At { get; }

    /// <summary>User name of the author, or a system actor such as <c>system:seed</c>.</summary>
    public string? By { get; }

    /// <summary>The Tunis calendar date of the change.</summary>
    public DateOnly Today => TunisTime.DateOf(At);
}
