using System.Globalization;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using MudBlazor;

namespace CarRental.Web.Components.Admin.Channels;

/// <summary>Screens of the channels area (secondary navigation).</summary>
public enum ChannelSection
{
    Channels = 0,
    ManualEntry = 1,
    Extranet = 2,
    Sync = 3,
    Log = 4,
    Reconciliation = 5,
}

/// <summary>Formatting shared by the channels screens (Tunis time, current culture).</summary>
public static class ChannelsFormat
{
    private const string LocalInputFormat = "yyyy-MM-ddTHH:mm";

    private static readonly string[] LocalInputFormats = ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"];

    /// <summary>Options of the dialogs: full width up to <paramref name="maxWidth"/>, closed by the Escape key.</summary>
    public static DialogOptions Dialog(MaxWidth maxWidth = MaxWidth.Small) =>
        new() { MaxWidth = maxWidth, FullWidth = true, CloseOnEscapeKey = true };

    public static string Amount(Money amount) => amount.ToString("N", CultureInfo.CurrentCulture);

    public static string Amount(Money? amount) => amount is { } value ? Amount(value) : "—";

    public static string WithSign(Money? amount) => amount switch
    {
        null => "—",
        { IsPositive: true } positive => "+" + Amount(positive),
        { } value => Amount(value),
    };

    public static string Instant(DateTimeOffset instant) => TunisTime.ToTunis(instant).ToString("g", CultureInfo.CurrentCulture);

    public static string Instant(DateTimeOffset? instant) => instant is { } value ? Instant(value) : "—";

    public static string Date(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);

    public static string Date(DateOnly? date) => date is { } value ? Date(value) : "—";

    /// <summary>A rate (0.15) as a percentage (15 %).</summary>
    public static string Percent(decimal rate) => (rate * 100m).ToString("0.##", CultureInfo.CurrentCulture) + " %";

    /// <summary>A Tunis wall-clock value for a <c>datetime-local</c> input.</summary>
    public static string ToLocalInput(DateTimeOffset instant) => TunisTime.ToTunis(instant).ToString(LocalInputFormat, CultureInfo.InvariantCulture);

    /// <summary>The instant of a <c>datetime-local</c> input value (Tunis wall clock), or <see langword="null"/>.</summary>
    public static DateTimeOffset? FromLocalInput(string? value) =>
        DateTime.TryParseExact(value, LocalInputFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? TunisTime.FromLocal(local)
            : null;

    public static DateOnly? ToDate(DateTime? value) => value is { } date ? DateOnly.FromDateTime(date) : null;

    public static DateTime ToDateTime(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);

    public static Color StateColor(ChannelState state) => state switch
    {
        ChannelState.Connected => Color.Success,
        ChannelState.Manual => Color.Info,
        _ => Color.Default,
    };

    public static string StateIcon(ChannelState state) => state switch
    {
        ChannelState.Connected => Icons.Material.Outlined.Hub,
        ChannelState.Manual => Icons.Material.Outlined.EditNote,
        _ => Icons.Material.Outlined.PauseCircle,
    };

    public static Color StatusColor(ChannelMessageStatus status) => status switch
    {
        ChannelMessageStatus.Accepted => Color.Success,
        ChannelMessageStatus.Refused => Color.Warning,
        ChannelMessageStatus.Rejected => Color.Error,
        _ => Color.Error,
    };

    public static Color SyncColor(ChannelSyncStatus status) => status switch
    {
        ChannelSyncStatus.Sent => Color.Success,
        ChannelSyncStatus.Failed => Color.Error,
        ChannelSyncStatus.Pending => Color.Warning,
        _ => Color.Default,
    };
}
