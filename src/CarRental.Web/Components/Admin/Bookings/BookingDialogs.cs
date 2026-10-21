using CarRental.Application.Bookings.Contracts;
using CarRental.Domain.Bookings;
using MudBlazor;

namespace CarRental.Web.Components.Admin.Bookings;

/// <summary>What <see cref="BookingReasonDialog"/> returns: the predefined reason (information requests) and the text.</summary>
internal sealed record BookingReasonInput(InformationRequestReason? Predefined, string Text);

/// <summary>What <see cref="CancelBookingDialog"/> returns.</summary>
internal sealed record CancellationInput(string Reason, bool WaiveFee);

/// <summary>A file dropped on the planning: onto a vehicle row, or back onto the lane of its category (no vehicle).</summary>
public sealed record PlanningDrop(PlanningBookingInfo Booking, Guid? VehicleId);

/// <summary>Shows the bookings dialogs with their parameters.</summary>
internal static class BookingDialogs
{
    /// <summary>Asks for a reason (or an optional text); <see langword="null"/> when cancelled.</summary>
    public static async Task<BookingReasonInput?> AskReasonAsync(
        this IDialogService dialogs,
        string title,
        string label,
        string submitText,
        string? message = null,
        bool danger = false,
        bool predefinedReasons = false,
        bool required = true,
        int maxLength = Booking.ReasonMaxLength)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        var parameters = new DialogParameters<BookingReasonDialog>
        {
            { d => d.Label, label },
            { d => d.SubmitText, submitText },
            { d => d.Message, message },
            { d => d.Danger, danger },
            { d => d.PredefinedReasons, predefinedReasons },
            { d => d.Required, required },
            { d => d.MaxLength, maxLength },
        };
        var dialog = await dialogs.ShowAsync<BookingReasonDialog>(title, parameters, BookingFormat.Dialog());
        return await dialog.Result is { Canceled: false, Data: BookingReasonInput input } ? input : null;
    }

    /// <summary>Asks to confirm an action; <see langword="true"/> when confirmed.</summary>
    public static async Task<bool> ConfirmAsync(this IDialogService dialogs, string title, string message, string confirmText, string cancelText)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        var confirmed = await dialogs.ShowMessageBoxAsync(title, message, yesText: confirmText, cancelText: cancelText, options: BookingFormat.Dialog());
        return confirmed == true;
    }
}
