using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Parties;
using CarRental.Application.Bookings.Writing;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings;

/// <summary>Reasons recorded by the platform itself, translated by the back office (resource <c>Reason.{key}</c>).</summary>
public static class BookingReasons
{
    /// <summary>A request refused by the internal customer check (never more specific: SPEC §5).</summary>
    public const string CustomerCheck = "booking.reason.customer_check";

    /// <summary>A booking modified or cancelled by its channel (<see cref="IChannelBookings"/>).</summary>
    public const string Channel = ChannelBookingReasons.Channel;

    /// <summary>Cancelled by the customer from "Ma réservation".</summary>
    public const string CustomerCancelled = "booking.reason.customer_cancelled";

    public static IReadOnlyList<string> All { get; } = [CustomerCheck, Channel, CustomerCancelled];
}

/// <summary>
/// The decisions on a rental file (docs/conception §2): validation of a web request (with the eligibility of the
/// drivers and the silent customer check), information requests, refusal, option confirmation, payment, cancellation
/// with its fee, and no-show. Commands that consume capacity go through the availability guard.
/// </summary>
public sealed class BookingWorkflowService
{
    private readonly BookingWriter _writer;
    private readonly DriverChecks _checks;
    private readonly ISettingsReader _settings;
    private readonly IOnlinePayments? _payments;

    internal BookingWorkflowService(BookingWriter writer, DriverChecks checks, ISettingsReader settings, IOnlinePayments? payments = null)
    {
        _writer = writer;
        _checks = checks;
        _settings = settings;
        _payments = payments;
    }

    /// <summary>
    /// <c>PendingValidation</c> → <c>Confirmed</c>, or <c>AwaitingPayment</c> in the payment link mode. A driver who
    /// fails the internal customer check gets the request refused with a generic reason; a driver who is not eligible
    /// blocks the validation (<see cref="BookingErrors.DriversNotEligible"/>), and so do documents sent online that
    /// wait for a review (<see cref="BookingErrors.SubmissionPending"/>).
    /// </summary>
    /// <remarks>
    /// In the <c>CaptureAtValidation</c> mode the pre-authorization of the request is captured first, outside any unit of
    /// work and before the transition (ADR-0051): a capture the bank has not confirmed yet keeps the request pending
    /// (<see cref="BookingErrors.CaptureUnconfirmed"/>), a refused one too (<see cref="BookingErrors.CaptureRejected"/>:
    /// validate with <see cref="ValidationPaymentChoice.PaymentLink"/>). A request without pre-authorization (online
    /// payment off at the submission, payment failed or abandoned) is validated in the payment link mode.
    /// </remarks>
    public Task<Result<ValidationOutcome>> ValidateAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        ValidateAsync(bookingId, ValidationPaymentChoice.Automatic, cancellationToken);

    /// <inheritdoc cref="ValidateAsync(Guid, CancellationToken)"/>
    public async Task<Result<ValidationOutcome>> ValidateAsync(Guid bookingId, ValidationPaymentChoice choice, CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.RequestsManage))
        {
            return BookingErrors.Forbidden;
        }

        var booking = await _writer.Db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        if (booking.Status != BookingStatus.PendingValidation)
        {
            return BookingErrors.InvalidTransition;
        }

        var drivers = booking.Parties.Drivers;
        if (await _checks.AnyFailsCustomerCheckAsync(drivers, cancellationToken))
        {
            var refused = await _writer.TransitionAsync(
                bookingId,
                (tracked, now, _) => Task.FromResult(tracked.Refuse(BookingReasons.CustomerCheck, _writer.Actor(), now)),
                cancellationToken);
            return refused.IsSuccess ? ValidationOutcome.RefusedAfterCustomerCheck : refused.Error;
        }

        var eligibility = await _checks.EligibilityAsync(drivers, booking.PickupAt, booking.ReturnAt, booking.AgencyId, cancellationToken);
        if (eligibility.IsFailure)
        {
            return eligibility.Error;
        }

        if (eligibility.Value.Any(report => !report.IsEligible))
        {
            return BookingErrors.DriversNotEligible;
        }

        if (await _checks.AnySubmissionPendingAsync(drivers, bookingId, cancellationToken))
        {
            return BookingErrors.SubmissionPending;
        }

        var mode = await _settings.GetAsync(BookingSettings.ValidationPaymentMode, booking.AgencyId, cancellationToken);
        var linkMode = mode == BookingValidationPaymentMode.PaymentLinkAfterValidation || choice == ValidationPaymentChoice.PaymentLink;
        if (!linkMode)
        {
            var captured = await CaptureAsync(booking, cancellationToken);
            if (captured.IsFailure)
            {
                return captured.Error;
            }

            linkMode = !captured.Value;
        }

        var validity = await _settings.GetAsync(BookingSettings.PaymentLinkValidity, booking.AgencyId, cancellationToken);
        var validated = await _writer.TransitionAsync(
            bookingId,
            (tracked, now, _) => Task.FromResult(tracked.Validate(linkMode ? now + validity : null, _writer.Actor(), now)),
            cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        return linkMode ? ValidationOutcome.AwaitingPayment : ValidationOutcome.Confirmed;
    }

    /// <summary>
    /// The capture step of a validation (never inside a unit of work): <see langword="true"/> when the pre-authorization
    /// of the request is captured (now or before), <see langword="false"/> when the request has none (payment link mode).
    /// </summary>
    private async Task<Result<bool>> CaptureAsync(Booking booking, CancellationToken cancellationToken)
    {
        if (_payments is null || !_payments.IsAvailable)
        {
            return false;
        }

        var guarantees = (await _payments.GetBookingOnlinePaymentsAsync(booking.Id, cancellationToken))
            .Where(p => p.Flow == OnlinePaymentFlowInfo.PreAuthorization)
            .ToList();
        if (guarantees.Any(p => p.Status is PaymentStatusInfo.Captured or PaymentStatusInfo.Refunded))
        {
            return true;
        }

        var held = guarantees.LastOrDefault(p => p.Status == PaymentStatusInfo.PreAuthorized);
        if (held is null)
        {
            return false;
        }

        // At most the pre-authorized amount; less when the quote went down since the submission.
        decimal? amount = booking.TotalInclVat < held.Amount ? booking.TotalInclVat.Amount : null;
        var capture = await _payments.CaptureBookingAsync(booking.Id, amount, cancellationToken);
        if (capture.IsSuccess)
        {
            return true;
        }

        return capture.Error.Code switch
        {
            "payments.online.capture_unconfirmed" => BookingErrors.CaptureUnconfirmed,
            "payments.gateway.rejected" => BookingErrors.CaptureRejected,
            _ => BookingErrors.PaymentUnavailable,
        };
    }

    /// <summary><c>PendingValidation</c> → <c>InformationRequested</c>: the hold is extended by <c>bookings.information_request_extension</c>.</summary>
    public async Task<Result> RequestInformationAsync(
        Guid bookingId,
        InformationRequestReason reason,
        string? message,
        CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.RequestsManage))
        {
            return BookingErrors.Forbidden;
        }

        return await _writer.TransitionAsync(
            bookingId,
            async (booking, now, ct) =>
            {
                var extension = await _settings.GetAsync(BookingSettings.InformationRequestExtension, booking.AgencyId, ct);
                return booking.RequestInformation(reason, message, extension, _writer.Actor(), now);
            },
            cancellationToken);
    }

    /// <summary>
    /// <c>InformationRequested</c> → <c>PendingValidation</c>, when the agent received the information by another way
    /// (re-checked under the availability lock).
    /// </summary>
    public async Task<Result> ProvideInformationAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.RequestsManage))
        {
            return BookingErrors.Forbidden;
        }

        return await _writer.TransitionAsync(
            bookingId,
            async (booking, now, ct) =>
            {
                var available = await EnsureStillAvailableAsync(booking, ct);
                return available.IsFailure ? available : booking.ProvideInformation(_writer.Actor(), now);
            },
            cancellationToken);
    }

    /// <summary><c>PendingValidation</c> or <c>InformationRequested</c> → <c>Refused</c>, with a reason.</summary>
    public async Task<Result> RefuseAsync(Guid bookingId, string? reason, CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.RequestsManage))
        {
            return BookingErrors.Forbidden;
        }

        return await _writer.TransitionAsync(
            bookingId,
            (booking, now, _) => Task.FromResult(booking.Refuse(reason, _writer.Actor(), now)),
            cancellationToken);
    }

    /// <summary><c>OptionHeld</c> → <c>Confirmed</c>, re-checked under the availability lock.</summary>
    public async Task<Result> ConfirmOptionAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.BookingsManage))
        {
            return BookingErrors.Forbidden;
        }

        return await _writer.TransitionAsync(
            bookingId,
            async (booking, now, ct) =>
            {
                var available = await EnsureStillAvailableAsync(booking, ct);
                return available.IsFailure ? available : booking.ConfirmOption(_writer.Actor(), now);
            },
            cancellationToken);
    }

    /// <summary><c>AwaitingPayment</c> → <c>Confirmed</c> (payment received); idempotent on a confirmed file.</summary>
    public async Task<Result> MarkPaidAsync(Guid bookingId, string? paymentReference, CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.BookingsManage) && !_writer.User.HasPermission(Permissions.PaymentsManage))
        {
            return BookingErrors.Forbidden;
        }

        var status = await _writer.Db.Set<Booking>().AsNoTracking().Where(b => b.Id == bookingId).Select(b => (BookingStatus?)b.Status).FirstOrDefaultAsync(cancellationToken);
        if (status == BookingStatus.Confirmed)
        {
            return Result.Success();
        }

        return await _writer.TransitionAsync(
            bookingId,
            (booking, now, _) => Task.FromResult(booking.MarkPaid(paymentReference, _writer.Actor(), now)),
            cancellationToken);
    }

    /// <summary>What cancelling the file now would cost (cancellation policy of its agency).</summary>
    public async Task<Result<CancellationPreview>> PreviewCancellationAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.BookingsRead))
        {
            return BookingErrors.Forbidden;
        }

        var booking = await _writer.Db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        var policy = await PolicyAsync(booking.AgencyId, cancellationToken);
        var fee = policy.FeeFor(booking.Status, booking.TotalInclVat, booking.PickupAt, _writer.Now());
        return new CancellationPreview(fee, policy.FreeUntil(booking.PickupAt), policy.FeePercent, fee.IsZero);
    }

    /// <summary>
    /// → <c>Cancelled</c> with a reason; the fee of the cancellation policy is recorded unless waived (permission
    /// <c>bookings.discount</c>). Releases the capacity.
    /// </summary>
    public async Task<Result<Money>> CancelAsync(Guid bookingId, string? reason, bool waiveFee = false, CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.BookingsManage) || (waiveFee && !_writer.User.HasPermission(Permissions.BookingsDiscount)))
        {
            return BookingErrors.Forbidden;
        }

        Money recorded = Money.Zero;
        var cancelled = await _writer.TransitionAsync(
            bookingId,
            async (booking, now, ct) =>
            {
                var policy = await PolicyAsync(booking.AgencyId, ct);
                var fee = policy.FeeFor(booking.Status, booking.TotalInclVat, booking.PickupAt, now);
                var outcome = booking.Cancel(reason, fee, waiveFee, _writer.Actor(), now);
                recorded = booking.CancellationFee ?? Money.Zero;
                return outcome;
            },
            cancellationToken);
        return cancelled.IsSuccess ? recorded : cancelled.Error;
    }

    /// <summary><c>Confirmed</c> → <c>NoShow</c>, with a reason, after <c>bookings.no_show_delay</c> past the planned pick-up.</summary>
    public async Task<Result> MarkNoShowAsync(Guid bookingId, string? reason, CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.BookingsManage))
        {
            return BookingErrors.Forbidden;
        }

        return await _writer.TransitionAsync(
            bookingId,
            async (booking, now, ct) =>
            {
                var delay = await _settings.GetAsync(BookingSettings.NoShowDelay, booking.AgencyId, ct);
                return booking.MarkNoShow(reason, delay, _writer.Actor(), now);
            },
            cancellationToken);
    }

    private async Task<CancellationPolicy> PolicyAsync(Guid agencyId, CancellationToken cancellationToken) => new(
        await _settings.GetAsync(BookingSettings.CancellationFreeUntilDays, agencyId, cancellationToken),
        await _settings.GetAsync(BookingSettings.CancellationFeePercent, agencyId, cancellationToken));

    /// <summary>The file, unchanged, still fits under the lock (§3: <c>ProvideInformation</c>, <c>ConfirmOption</c>).</summary>
    private Task<Result> EnsureStillAvailableAsync(Booking booking, CancellationToken cancellationToken) =>
        _writer.Guard.EnsureAvailableAsync(
            new AvailabilityRequest(booking.CategoryId, booking.Channel, booking.OccupancyAfter(booking.Rental, booking.AssignedVehicleId))
            {
                ComplianceWaived = booking.IsComplianceWaived,
            },
            cancellationToken: cancellationToken);
}

/// <summary><see cref="IBookingPaymentStatus"/> for the Payments module (system actor).</summary>
internal sealed class BookingPaymentStatus(BookingWorkflowService workflow) : IBookingPaymentStatus
{
    public Task<Result> MarkPaidAsync(Guid bookingId, string? paymentReference, CancellationToken cancellationToken = default) =>
        workflow.MarkPaidAsync(bookingId, paymentReference, cancellationToken);
}
