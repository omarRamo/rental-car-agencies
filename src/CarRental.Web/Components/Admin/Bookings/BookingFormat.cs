using System.Globalization;
using CarRental.Application.Bookings.Administration;
using CarRental.Application.Bookings.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using CarRental.Web.Components.Admin.Pricing;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace CarRental.Web.Components.Admin.Bookings;

/// <summary>Display formats of the bookings screens (current UI culture; instants shown in Tunis time).</summary>
internal static class BookingFormat
{
    private const string LocalInputFormat = "yyyy-MM-dd'T'HH:mm";

    /// <summary>Options of the bookings dialogs: full width up to <paramref name="maxWidth"/>, closed by the Escape key.</summary>
    public static DialogOptions Dialog(MaxWidth maxWidth = MaxWidth.Small) =>
        new() { MaxWidth = maxWidth, FullWidth = true, CloseOnEscapeKey = true };

    public static string Amount(Money amount) => amount.ToString("N", CultureInfo.CurrentCulture);

    public static string Amount(Money? amount) => amount is { } value ? Amount(value) : "—";

    public static string Instant(DateTimeOffset instant) => TunisTime.ToTunis(instant).ToString("g", CultureInfo.CurrentCulture);

    public static string Instant(DateTimeOffset? instant) => instant is { } value ? Instant(value) : "—";

    public static string Time(DateTimeOffset instant) => TunisTime.ToTunis(instant).ToString("t", CultureInfo.CurrentCulture);

    public static string Date(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);

    public static string DayHeader(DateOnly date) => date.ToString("ddd d", CultureInfo.CurrentCulture);

    /// <summary>A duration such as <c>3 h 05</c> or <c>2 j 4 h</c> (localized by the resources of the caller).</summary>
    public static string Duration(IStringLocalizer localizer, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (duration <= TimeSpan.Zero)
        {
            return localizer["Duration.Over"];
        }

        return duration.TotalDays >= 1
            ? localizer["Duration.DaysHours", (int)duration.TotalDays, duration.Hours]
            : localizer["Duration.HoursMinutes", (int)duration.TotalHours, duration.Minutes.ToString("D2", CultureInfo.InvariantCulture)];
    }

    /// <summary>The value of a <c>datetime-local</c> input for an instant (Tunis wall clock).</summary>
    public static string ToLocalInput(DateTimeOffset instant) =>
        TunisTime.ToTunis(instant).ToString(LocalInputFormat, CultureInfo.InvariantCulture);

    /// <summary>The instant of a <c>datetime-local</c> input value (Tunis wall clock), or <see langword="null"/>.</summary>
    public static DateTimeOffset? FromLocalInput(string? value) =>
        DateTime.TryParseExact(value, LocalInputFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? TunisTime.FromLocal(local)
            : null;

    /// <summary>
    /// The label of a frozen quote line: the pricing keys from <paramref name="quotes"/>, the keys of the Bookings module
    /// (<c>BookingQuote.*</c>) from <paramref name="bookings"/>; numbers shown in the current culture.
    /// </summary>
    public static string LineLabel(IStringLocalizer quotes, IStringLocalizer bookings, QuoteLineView line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return PricingDisplay.QuoteLabel(BookingQuoteLabels.IsBookingLabel(line.LabelKey) ? bookings : quotes, line.LabelKey, line.LabelArguments);
    }

    /// <summary>The text of a reason: a well-known key of the platform is translated, an agent's text shown as typed.</summary>
    public static string Reason(IStringLocalizer localizer, string? reason)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (string.IsNullOrEmpty(reason))
        {
            return string.Empty;
        }

        var text = localizer[$"Reason.{reason}"];
        return text.ResourceNotFound ? reason : text.Value;
    }

    /// <summary>The domain status of a contract status (same values).</summary>
    public static BookingStatus ToStatus(BookingStatusInfo status) => (BookingStatus)(int)status;

    public static string StatusIcon(BookingStatus status) => status switch
    {
        BookingStatus.Draft => Icons.Material.Outlined.EditNote,
        BookingStatus.PendingValidation => Icons.Material.Outlined.HourglassTop,
        BookingStatus.InformationRequested => Icons.Material.Outlined.HelpOutline,
        BookingStatus.OptionHeld => Icons.Material.Outlined.Schedule,
        BookingStatus.AwaitingPayment => Icons.Material.Outlined.Payments,
        BookingStatus.Confirmed => Icons.Material.Outlined.CheckCircle,
        BookingStatus.InProgress => Icons.Material.Outlined.DirectionsCar,
        BookingStatus.Returned => Icons.Material.Outlined.AssignmentReturn,
        BookingStatus.Closed => Icons.Material.Outlined.Lock,
        BookingStatus.Refused => Icons.Material.Outlined.Block,
        BookingStatus.Expired => Icons.Material.Outlined.TimerOff,
        BookingStatus.Cancelled => Icons.Material.Outlined.Cancel,
        _ => Icons.Material.Outlined.PersonOff,
    };

    /// <summary>The CSS modifier of a status (planning bars and status pills): colour plus a pattern, never colour alone.</summary>
    public static string StatusClass(BookingStatus status) => status switch
    {
        BookingStatus.PendingValidation or BookingStatus.InformationRequested => "pending",
        BookingStatus.OptionHeld or BookingStatus.AwaitingPayment => "option",
        BookingStatus.Confirmed => "confirmed",
        BookingStatus.InProgress => "progress",
        BookingStatus.Returned or BookingStatus.Closed => "returned",
        _ => "ended",
    };
}
