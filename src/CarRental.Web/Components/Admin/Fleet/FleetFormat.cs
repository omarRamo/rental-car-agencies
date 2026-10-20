using System.Globalization;
using CarRental.Application.Fleet;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using MudBlazor;

namespace CarRental.Web.Components.Admin.Fleet;

/// <summary>Display formats of the fleet screens (current UI culture; instants shown in Tunis time).</summary>
public static class FleetFormat
{
    /// <summary>A notice paragraph: dark text and a red edge (the outlined MudBlazor alerts miss the 4.5:1 contrast).</summary>
    public const string NoticeStyle = "margin:0;padding:0.5rem 0.75rem;border-inline-start:4px solid #b91c1c;background-color:#fef2f2;color:#1b1f24";

    /// <summary>Options of the fleet dialogs: full width up to <paramref name="maxWidth"/>, closed by the Escape key (modal dialog pattern).</summary>
    public static DialogOptions Dialog(MaxWidth maxWidth = MaxWidth.Small) =>
        new() { MaxWidth = maxWidth, FullWidth = true, CloseOnEscapeKey = true };

    public static string Amount(Money amount) => amount.ToString("N", CultureInfo.CurrentCulture);

    public static string Amount(Money? amount) => amount is { } value ? Amount(value) : "—";

    public static string Date(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);

    public static string Date(DateOnly? date) => date is { } value ? Date(value) : "—";

    public static string Instant(DateTimeOffset instant) => TunisTime.ToTunis(instant).ToString("g", CultureInfo.CurrentCulture);

    public static string Instant(DateTimeOffset? instant) => instant is { } value ? Instant(value) : "—";

    /// <summary>The last Tunis day before an exclusive end instant (a compliance end is the midnight after the expiry day).</summary>
    public static string LastDay(DateTimeOffset exclusiveEnd) => Date(TunisTime.DateOf(exclusiveEnd.AddTicks(-1)));

    public static string Kilometres(int kilometres) => string.Create(CultureInfo.CurrentCulture, $"{kilometres:N0} km");

    public static string Kilometres(int? kilometres) => kilometres is { } value ? Kilometres(value) : "—";

    /// <summary>A Tunis wall-clock date and time from the pickers, as an instant.</summary>
    public static DateTimeOffset ToInstant(DateTime date, TimeSpan? time) =>
        TunisTime.FromLocal(DateOnly.FromDateTime(date), TimeOnly.FromTimeSpan(time ?? TimeSpan.Zero));

    public static DateTime ToPickerDate(DateTimeOffset instant) => TunisTime.ToTunis(instant).Date;

    public static TimeSpan ToPickerTime(DateTimeOffset instant) => TunisTime.ToTunis(instant).TimeOfDay;

    public static BadgeTone ToneOf(VehicleStatus status) => status switch
    {
        VehicleStatus.Active => BadgeTone.Success,
        VehicleStatus.Maintenance => BadgeTone.Warning,
        VehicleStatus.OutOfService => BadgeTone.Danger,
        _ => BadgeTone.Neutral,
    };

    public static BadgeTone ToneOf(DeadlineState state) => state switch
    {
        DeadlineState.Ok => BadgeTone.Success,
        DeadlineState.DueSoon => BadgeTone.Warning,
        _ => BadgeTone.Danger,
    };

    public static BadgeTone ToneOf(MaintenanceDueState state) => state switch
    {
        MaintenanceDueState.Ok => BadgeTone.Success,
        MaintenanceDueState.DueSoon => BadgeTone.Warning,
        _ => BadgeTone.Danger,
    };

    public static BadgeTone ToneOf(MaintenanceStatus status) => status switch
    {
        MaintenanceStatus.Planned => BadgeTone.Info,
        MaintenanceStatus.InProgress => BadgeTone.Warning,
        MaintenanceStatus.Completed => BadgeTone.Success,
        _ => BadgeTone.Neutral,
    };
}

/// <summary>Colour family of a <see cref="FleetBadge"/>: red for blocking, orange for soon, green for fine.</summary>
public enum BadgeTone
{
    Neutral = 0,
    Info = 1,
    Success = 2,
    Warning = 3,
    Danger = 4,
}
