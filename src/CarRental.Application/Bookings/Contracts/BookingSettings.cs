using CarRental.Application.Referential.Contracts;

namespace CarRental.Application.Bookings.Contracts;

/// <summary>
/// Business settings of the Bookings module (docs/conception/phase-1-exploitation.md §1), declared to the Referential
/// catalogue by <c>AddBookingsModule()</c> and read per agency. The hold of a web request
/// (<see cref="WellKnownSettings.BookingHoldDuration"/>) and the preparation buffer
/// (<see cref="WellKnownSettings.PreparationBuffer"/>) are platform settings declared by the Referential module.
/// </summary>
public static class BookingSettings
{
    /// <summary>Extension of the hold when an agent asks the customer for more information (24 h).</summary>
    public static readonly SettingDefinition<TimeSpan> InformationRequestExtension =
        SettingDefinitions.Duration("bookings.information_request_extension", TimeSpan.FromHours(24));

    /// <summary>Default duration of a dated phone option; the agent may choose another expiry (48 h).</summary>
    public static readonly SettingDefinition<TimeSpan> OptionDefaultDuration =
        SettingDefinitions.Duration("bookings.option_default_duration", TimeSpan.FromHours(48));

    /// <summary>Fallback payment mode: time to pay after the validation (<c>AwaitingPayment</c>), 24 h.</summary>
    public static readonly SettingDefinition<TimeSpan> PaymentLinkValidity =
        SettingDefinitions.Duration("bookings.payment_link_validity", TimeSpan.FromHours(24));

    /// <summary>Delay after the planned pick-up beyond which a no-show may be recorded (120 min).</summary>
    public static readonly SettingDefinition<TimeSpan> NoShowDelay =
        SettingDefinitions.Duration("bookings.no_show_delay", TimeSpan.FromMinutes(120));

    /// <summary>A web request still pending after this delay is escalated to the managers (120 min).</summary>
    public static readonly SettingDefinition<TimeSpan> EscalationDelay =
        SettingDefinitions.Duration("bookings.escalation_delay", TimeSpan.FromMinutes(120));

    /// <summary>A confirmed booking is cancelled for free until D-x, Tunis calendar days (3).</summary>
    public static readonly SettingDefinition<int> CancellationFreeUntilDays =
        SettingDefinitions.WholeNumber("bookings.cancellation_free_until_days", 3, 0, 365);

    /// <summary>Fee of a later cancellation, ratio of the total TTC of the quote (0.20).</summary>
    public static readonly SettingDefinition<decimal> CancellationFeePercent =
        SettingDefinitions.Number("bookings.cancellation_fee_percent", 0.20m, 0m, 1m);

    /// <summary>
    /// What the validation of a web request does (SPEC §9): <see cref="BookingValidationPaymentMode.CaptureAtValidation"/>
    /// confirms it (the pre-authorization taken at the submission is captured — phase 2), the fallback
    /// <see cref="BookingValidationPaymentMode.PaymentLinkAfterValidation"/> waits for the payment through a link
    /// (<c>AwaitingPayment</c>, until <see cref="PaymentLinkValidity"/>).
    /// </summary>
    public static readonly SettingDefinition<BookingValidationPaymentMode> ValidationPaymentMode = new(
        "bookings.validation_payment_mode",
        BookingValidationPaymentMode.CaptureAtValidation,
        value => value.Length > 0
                 && value.All(char.IsAsciiLetter)
                 && Enum.TryParse<BookingValidationPaymentMode>(value, ignoreCase: false, out var mode)
                 && Enum.IsDefined(mode)
            ? mode
            : SettingErrors.InvalidValue("bookings.validation_payment_mode"),
        mode => mode.ToString());

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        InformationRequestExtension,
        OptionDefaultDuration,
        PaymentLinkValidity,
        NoShowDelay,
        EscalationDelay,
        CancellationFreeUntilDays,
        CancellationFeePercent,
        ValidationPaymentMode,
    ];
}

/// <summary>Payment step of the validation of a web request (stored by name).</summary>
public enum BookingValidationPaymentMode
{
    /// <summary>Normal mode: the request is confirmed; the pre-authorization is captured (phase 2, Clic to Pay).</summary>
    CaptureAtValidation = 1,

    /// <summary>Fallback mode: the validated request waits for its payment by link (<c>AwaitingPayment</c>).</summary>
    PaymentLinkAfterValidation = 2,
}
