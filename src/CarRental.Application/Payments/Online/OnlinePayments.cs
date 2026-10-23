using CarRental.Application.Accounting.Contracts;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Payments.Online;

/// <summary>
/// The online payment flows (<see cref="IOnlinePayments"/>, ADR-0039). The gateway is always called outside any database
/// transaction: a payment (or an operation) is recorded first, the gateway is called, then its answer is applied in a
/// second unit of work — so a replayed transaction never repeats a call, and a call whose answer is lost is resolved by
/// reading the status by order number. The status of a payment only changes from a status read server to server
/// (<see cref="Payment.ApplyConfirmedGatewayStatus"/>); concurrent returns and job runs apply it once (row version, then
/// the replay of the same status is a no-op). Each confirmed change is told to the other modules by an
/// <see cref="OnlinePaymentStatusChanged"/> message written in the same transaction.
/// </summary>
internal sealed partial class OnlinePayments(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IOutbox outbox,
    IUserContext userContext,
    IClicToPayGateway gateway,
    IReferentialQueries referential,
    IBookingsQueries bookings,
    ISettingsReader settings,
    TimeProvider timeProvider,
    ILogger<OnlinePayments> logger,
    IAccountingPeriods periods) : IOnlinePayments
{
    /// <summary>A second start for the same booking within this delay returns the page of the first (double submission).</summary>
    public static readonly TimeSpan AttemptReuseWindow = TimeSpan.FromMinutes(10);

    /// <summary>A gateway request left without answer is sent again only after this delay (and a status read showing no effect).</summary>
    public static readonly TimeSpan OperationGrace = TimeSpan.FromMinutes(2);

    /// <summary>An acknowledged capture or reversal the gateway still does not show after this delay is flagged.</summary>
    public static readonly TimeSpan UnconfirmedOperationDelay = TimeSpan.FromMinutes(15);

    /// <summary>A sent refund still unconfirmed after this delay is flagged for a check in the merchant portal.</summary>
    public static readonly TimeSpan RefundConfirmationDelay = TimeSpan.FromHours(24);

    private const int ConcurrencyAttempts = 3;

    public bool IsAvailable => gateway.IsEnabled;

    private AuditActor Actor => userContext.ToAuditActor();

    /// <summary>Approving a refund above the threshold takes a person holding the permission (never the system actor).</summary>
    private bool CanApprove => !userContext.IsSystem && userContext.HasPermission(Permissions.PaymentsRefundsApprove);

    public Task<Result<OnlinePaymentRedirect>> StartPreAuthorizationAsync(OnlinePaymentRequest request, CancellationToken cancellationToken = default) =>
        StartAsync(request, OnlinePaymentFlow.PreAuthorization, cancellationToken);

    public Task<Result<OnlinePaymentRedirect>> StartPaymentAsync(OnlinePaymentRequest request, CancellationToken cancellationToken = default) =>
        StartAsync(request, OnlinePaymentFlow.OneStep, cancellationToken);

    public async Task<Result<OnlinePaymentReturn>> HandleReturnAsync(OnlinePaymentReturnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureOutsideUnitOfWork();
        var payment = await FindByOrderAsync(request.OrderNumber, request.GatewayOrderId, cancellationToken);
        if (payment is null)
        {
            return PaymentsErrors.OnlinePaymentNotFound;
        }

        // A payment already settled in a confirmed state is not read again (reloads of the page cost nothing).
        var confirmed = !NeedsStatusRead(payment);
        if (!confirmed && gateway.IsEnabled)
        {
            var refreshed = await RefreshCoreAsync(payment.Id, cancellationToken);
            if (refreshed.IsSuccess)
            {
                payment = refreshed.Value;
                confirmed = true;
            }
        }

        var path = payment.Status switch
        {
            PaymentStatus.PreAuthorized or PaymentStatus.Captured or PaymentStatus.Refunded => payment.GatewayReturnPath,
            PaymentStatus.Failed or PaymentStatus.Cancelled => payment.GatewayFailPath,
            _ => request.FromFailureAddress ? payment.GatewayFailPath : payment.GatewayReturnPath,
        };
        var redirect = (path ?? "/").Replace(OnlinePaymentRequest.PaymentIdPlaceholder, payment.Id.ToString("D"), StringComparison.Ordinal);
        return new OnlinePaymentReturn(payment.Id, payment.BookingId, payment.Status.ToInfo(), confirmed, redirect);
    }

    public async Task<Result<OnlinePaymentInfo>> CaptureAsync(Guid paymentId, decimal? amount = null, CancellationToken cancellationToken = default)
    {
        EnsureOutsideUnitOfWork();
        if (!gateway.IsEnabled)
        {
            return PaymentsErrors.GatewayDisabled;
        }

        Money? requested = null;
        if (amount is { } value)
        {
            var parsed = PaymentInput.Amount(value);
            if (parsed.IsFailure)
            {
                return parsed.Error;
            }

            requested = parsed.Value;
        }

        var current = await CurrentAsync(paymentId, cancellationToken);
        if (current.IsFailure)
        {
            return current.Error;
        }

        var (payment, readError) = current.Value;
        if (payment.OnlineFlow != OnlinePaymentFlow.PreAuthorization)
        {
            return PaymentsErrors.OnlineCaptureNotApplicable;
        }

        if (payment.Status is PaymentStatus.Captured or PaymentStatus.Refunded)
        {
            return await InfoAsync(payment, cancellationToken);
        }

        if (readError is not null)
        {
            // The status could not be read: nothing is sent on the faith of a state that may be out of date.
            return readError;
        }

        if (payment.Status != PaymentStatus.PreAuthorized)
        {
            return PaymentsErrors.OnlineNotPreAuthorized;
        }

        var target = requested ?? payment.Amount;
        if (target > payment.Amount)
        {
            return PaymentsErrors.OnlineCaptureExceedsAuthorization;
        }

        if (payment.ExternalReference is not { } gatewayOrderId)
        {
            return PaymentsErrors.OnlinePaymentNotFound;
        }

        // Monthly closing (ADR-0065): no capture dated in a closed month.
        var open = await periods.EnsureOpenAsync(payment.AgencyId, TunisTime.Today(timeProvider), cancellationToken);
        if (open.IsFailure)
        {
            return open.Error;
        }

        var marked = await MarkCaptureAsync(payment, target, cancellationToken);
        if (marked.IsFailure)
        {
            return marked.Error;
        }

        var deposit = await gateway.DepositAsync(new GatewayOrderOperation(payment.OrderNumber, gatewayOrderId, marked.Value), cancellationToken);
        if (deposit.IsSuccess)
        {
            await ChangeAsync(payment.Id, p => p.AcknowledgeGatewayOperation(PendingGatewayOperation.Capture), cancellationToken);
        }
        else if (deposit.Error == PaymentsErrors.GatewayRejected)
        {
            await ChangeAsync(
                payment.Id,
                p => p.AbandonGatewayOperation(PendingGatewayOperation.Capture, GatewayAttention.OperationRejected, "deposit.do refused", timeProvider.GetUtcNow()),
                cancellationToken);
            await RefreshCoreAsync(payment.Id, cancellationToken);
            return PaymentsErrors.GatewayRejected;
        }

        // Always read the status: after a lost answer the deposit may have been processed (never sent twice blindly).
        var refreshed = await RefreshCoreAsync(payment.Id, cancellationToken);
        if (refreshed.IsSuccess && refreshed.Value.Status is PaymentStatus.Captured or PaymentStatus.Refunded)
        {
            return await InfoAsync(refreshed.Value, cancellationToken);
        }

        return deposit.IsSuccess ? PaymentsErrors.OnlineCaptureUnconfirmed : deposit.Error;
    }

    public async Task<Result<OnlinePaymentInfo>> CaptureBookingAsync(Guid bookingId, decimal? amount = null, CancellationToken cancellationToken = default)
    {
        var candidates = await OnlineQuery()
            .Where(p => p.BookingId == bookingId && p.OnlineFlow == OnlinePaymentFlow.PreAuthorization)
            .OrderByDescending(p => p.RecordedAtUtc)
            .Select(p => new { p.Id, p.Status, p.PendingGatewayOperation })
            .ToListAsync(cancellationToken);
        var target = candidates.FirstOrDefault(p => p.Status == PaymentStatus.PreAuthorized)
            ?? candidates.FirstOrDefault(p => p.Status is PaymentStatus.Captured or PaymentStatus.Refunded)
            ?? candidates.FirstOrDefault(p => p.Status == PaymentStatus.Pending);
        return target is null ? PaymentsErrors.OnlineNotPreAuthorized : await CaptureAsync(target.Id, amount, cancellationToken);
    }

    public async Task<Result<OnlinePaymentInfo>> ReverseAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        EnsureOutsideUnitOfWork();
        if (!gateway.IsEnabled)
        {
            return PaymentsErrors.GatewayDisabled;
        }

        var current = await CurrentAsync(paymentId, cancellationToken);
        if (current.IsFailure)
        {
            return current.Error;
        }

        var reversed = await ReleaseAsync(current.Value, cancellationToken);
        return reversed.IsFailure ? reversed.Error : await InfoAsync(reversed.Value, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<OnlinePaymentInfo>>> ReverseBookingAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        EnsureOutsideUnitOfWork();
        if (!gateway.IsEnabled)
        {
            return PaymentsErrors.GatewayDisabled;
        }

        var ids = await OnlineQuery()
            .Where(p => p.BookingId == bookingId && (p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.PreAuthorized))
            .OrderBy(p => p.RecordedAtUtc)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        // Record the release of every payment not made yet first (database only), then talk to the gateway: what cannot
        // be confirmed now is completed by the catch-up job.
        foreach (var id in ids)
        {
            await ChangeAsync(
                id,
                p => p.Status == PaymentStatus.Pending && p.PendingGatewayOperation is null ? p.RequestGatewayReverse(timeProvider.GetUtcNow()) : Result.Success(),
                cancellationToken);
        }

        foreach (var id in ids)
        {
            var current = await CurrentAsync(id, cancellationToken);
            if (current.IsSuccess)
            {
                var reversed = await ReleaseAsync(current.Value, cancellationToken);
                if (reversed.IsFailure)
                {
                    LogReleaseDeferred(id, reversed.Error.Code);
                }
            }
        }

        return Result.Success<IReadOnlyList<OnlinePaymentInfo>>(await GetBookingOnlinePaymentsAsync(bookingId, cancellationToken));
    }

    public async Task<Result<OnlineRefundOutcome>> RefundAsync(OnlineRefundRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureOutsideUnitOfWork();
        if (!gateway.IsEnabled)
        {
            return PaymentsErrors.GatewayDisabled;
        }

        var amount = PaymentInput.Amount(request.Amount);
        if (amount.IsFailure)
        {
            return amount.Error;
        }

        var current = await CurrentAsync(request.PaymentId, cancellationToken);
        if (current.IsFailure)
        {
            return current.Error;
        }

        // Monthly closing (ADR-0065): no refund dated in a closed month.
        var open = await periods.EnsureOpenAsync(current.Value.Payment.AgencyId, TunisTime.Today(timeProvider), cancellationToken);
        if (open.IsFailure)
        {
            return open.Error;
        }

        var threshold = await settings.GetAsync(PaymentSettings.OnlineRefundApprovalThreshold, current.Value.Payment.AgencyId, cancellationToken);
        var canApprove = CanApprove;
        var created = await ChangeAsync<Guid>(
            async ct =>
            {
                var original = await db.FindPaymentAsync(request.PaymentId, ct);
                if (original is null)
                {
                    return PaymentsErrors.PaymentNotFound;
                }

                // The threshold applies to the refunds of the payment added up: splitting a refund never avoids the approval.
                var requiresApproval = original.RefundedAmount + original.PendingRefundAmount + amount.Value > threshold;
                var now = timeProvider.GetUtcNow();
                var requested = original.RequestOnlineRefund(amount.Value, request.Reason, requiresApproval, Actor, now);
                if (requested.IsFailure)
                {
                    return requested.Error;
                }

                var refund = requested.Value;
                if (requiresApproval && canApprove)
                {
                    var approved = refund.ApproveOnlineRefund(Actor, now);
                    if (approved.IsFailure)
                    {
                        return approved.Error;
                    }
                }

                if (!refund.IsAwaitingApproval)
                {
                    var sent = refund.MarkOnlineRefundSent(now);
                    if (sent.IsFailure)
                    {
                        return sent.Error;
                    }
                }

                db.Set<Payment>().Add(refund);
                return refund.Id;
            },
            cancellationToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var stored = await db.Set<Payment>().AsNoTracking().FirstAsync(p => p.Id == created.Value, cancellationToken);
        return stored.IsAwaitingApproval
            ? new OnlineRefundOutcome(stored.Id, request.PaymentId, OnlineRefundStateInfo.AwaitingApproval)
            : await SendRefundAsync(created.Value, cancellationToken);
    }

    public async Task<Result<OnlineRefundOutcome>> ApproveRefundAsync(Guid refundId, CancellationToken cancellationToken = default)
    {
        EnsureOutsideUnitOfWork();
        if (!CanApprove)
        {
            return PaymentsErrors.RefundApprovalForbidden;
        }

        if (!gateway.IsEnabled)
        {
            return PaymentsErrors.GatewayDisabled;
        }

        var approved = await ChangeAsync<Guid>(
            async ct =>
            {
                var refund = await db.FindPaymentAsync(refundId, ct);
                if (refund is null || refund.Direction != PaymentDirection.Refund || !refund.IsOnline)
                {
                    return PaymentsErrors.PaymentNotFound;
                }

                var now = timeProvider.GetUtcNow();
                var approval = refund.ApproveOnlineRefund(Actor, now);
                if (approval.IsFailure)
                {
                    return approval.Error;
                }

                var sent = refund.MarkOnlineRefundSent(now);
                return sent.IsFailure ? sent.Error : refund.Id;
            },
            cancellationToken);
        return approved.IsFailure ? approved.Error : await SendRefundAsync(refundId, cancellationToken);
    }

    public async Task<Result> RejectRefundAsync(Guid refundId, string reason, CancellationToken cancellationToken = default)
    {
        EnsureOutsideUnitOfWork();
        if (!CanApprove)
        {
            return PaymentsErrors.RefundApprovalForbidden;
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < Payment.ReasonMinLength)
        {
            return PaymentsErrors.ReasonRequired;
        }

        if (reason.Trim().Length > Payment.ReasonMaxLength)
        {
            return PaymentsErrors.ReasonTooLong;
        }

        var rejected = await ChangeAsync<Guid>(
            async ct =>
            {
                var refund = await db.FindPaymentAsync(refundId, ct);
                if (refund is null || refund.Direction != PaymentDirection.Refund || !refund.IsOnline || refund.OriginalPaymentId is not { } originalId)
                {
                    return PaymentsErrors.PaymentNotFound;
                }

                if (!refund.IsAwaitingApproval)
                {
                    return refund.Status == PaymentStatus.Pending ? PaymentsErrors.RefundNotAwaitingApproval : PaymentsErrors.RefundNotPending;
                }

                var original = await db.FindPaymentAsync(originalId, ct);
                if (original is null)
                {
                    return PaymentsErrors.PaymentNotFound;
                }

                var released = original.ReleaseOnlineRefund(refund, PaymentStatus.Cancelled, reason, Actor, timeProvider.GetUtcNow());
                return released.IsFailure ? released.Error : refund.Id;
            },
            cancellationToken);
        return rejected.IsFailure ? rejected.Error : Result.Success();
    }

    public async Task<Result<OnlinePaymentInfo>> RefreshStatusAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        EnsureOutsideUnitOfWork();
        if (!gateway.IsEnabled)
        {
            return PaymentsErrors.GatewayDisabled;
        }

        var refreshed = await RefreshCoreAsync(paymentId, cancellationToken);
        return refreshed.IsFailure ? refreshed.Error : await InfoAsync(refreshed.Value, cancellationToken);
    }

    public async Task<OnlinePaymentInfo?> FindAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        var payment = await OnlineQuery().FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        return payment is null ? null : (await InfoAsync(payment, cancellationToken)).Value;
    }

    public async Task<IReadOnlyList<OnlinePaymentInfo>> GetBookingOnlinePaymentsAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var payments = await OnlineQuery()
            .Where(p => p.BookingId == bookingId)
            .OrderBy(p => p.RecordedAtUtc)
            .ThenBy(p => p.Id)
            .ToListAsync(cancellationToken);
        var ids = payments.Select(p => p.Id).ToList();
        var refunds = await OnlineRefunds(ids).ToListAsync(cancellationToken);
        return [.. payments.Select(p => p.ToOnlineInfo(refunds))];
    }

    /// <summary>
    /// Catch-up of one payment (job every 15 minutes): reads and applies its status, sends again a release left without
    /// answer, gives up a capture the gateway does not show, and flags operations that stay unconfirmed.
    /// </summary>
    internal async Task CatchUpAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var refreshed = await RefreshCoreAsync(paymentId, cancellationToken);
        if (refreshed.IsFailure)
        {
            return;
        }

        var payment = refreshed.Value;
        var now = timeProvider.GetUtcNow();
        var age = payment.PendingGatewaySinceUtc is { } since ? now - since : TimeSpan.Zero;
        switch (payment.Status, payment.PendingGatewayOperation, payment.PendingGatewayAcknowledged)
        {
            case (PaymentStatus.PreAuthorized, PendingGatewayOperation.Reverse, false):
                // A release recorded before the customer paid, or sent without answer: sent (again) unless just sent.
                await ReverseCoreAsync(payment, cancellationToken);
                break;
            case (PaymentStatus.PreAuthorized, PendingGatewayOperation.Reverse, true) when age >= UnconfirmedOperationDelay:
                await ChangeAsync(payment.Id, p => p.FlagGatewayAttention(GatewayAttention.ReversalNotConfirmed, "reverse.do acknowledged, status still pre-authorized", now), cancellationToken);
                break;
            case (PaymentStatus.PreAuthorized, PendingGatewayOperation.Capture, false) when age >= OperationGrace:
                // Kept with its amount (a late deposit is then recorded with it); the validation decides to send it again.
                await ChangeAsync(payment.Id, p => p.FlagGatewayAttention(GatewayAttention.CaptureNotConfirmed, "deposit.do without answer, status still pre-authorized", now), cancellationToken);
                break;
            case (PaymentStatus.PreAuthorized, PendingGatewayOperation.Capture, true) when age >= UnconfirmedOperationDelay:
                await ChangeAsync(payment.Id, p => p.FlagGatewayAttention(GatewayAttention.CaptureNotConfirmed, "deposit.do acknowledged, status still pre-authorized", now), cancellationToken);
                break;
        }

        var stale = await db.Set<Payment>()
            .AsNoTracking()
            .AnyAsync(
                r => r.OriginalPaymentId == paymentId && r.Method == PaymentMethod.ClicToPay && r.Direction == PaymentDirection.Refund
                     && r.Status == PaymentStatus.Pending && r.PendingGatewayOperation == PendingGatewayOperation.Refund
                     && r.PendingGatewaySinceUtc <= now - RefundConfirmationDelay,
                cancellationToken);
        if (stale)
        {
            await ChangeAsync(paymentId, p => p.FlagGatewayAttention(GatewayAttention.RefundNotConfirmed, "refund.do sent, outcome not confirmed", now), cancellationToken);
        }
    }

    /// <summary>
    /// Daily reconciliation of one payment: applies its status like the catch-up, then compares the amounts reported by
    /// the gateway with the recorded ones and flags a difference (TODO(spec): merchant statement of SMT).
    /// </summary>
    internal async Task<Result> ReconcileAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(paymentId, cancellationToken);
        if (current is null)
        {
            return PaymentsErrors.OnlinePaymentNotFound;
        }

        var read = await gateway.GetOrderStatusExtendedAsync(new GatewayOrderReference(current.OrderNumber, current.ExternalReference), cancellationToken);
        if (read.IsFailure)
        {
            return read.Error;
        }

        var applied = await ApplyReadAsync(paymentId, read.Value, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        var payment = applied.Value;
        var status = read.Value;
        var now = timeProvider.GetUtcNow();
        if (payment.Status is PaymentStatus.Captured or PaymentStatus.Refunded)
        {
            if (status.DepositedAmount is { } deposited && deposited != payment.CapturedAmount)
            {
                return await ChangeAsync(paymentId, p => p.FlagGatewayAttention(GatewayAttention.AmountMismatch, Amounts("deposited", deposited, p.CapturedAmount), now), cancellationToken);
            }

            if (status.RefundedAmount is { } refunded && refunded != payment.RefundedAmount && payment.PendingRefundAmount.IsZero)
            {
                return await ChangeAsync(paymentId, p => p.FlagGatewayAttention(GatewayAttention.AmountMismatch, Amounts("refunded", refunded, p.RefundedAmount), now), cancellationToken);
            }
        }

        if (payment.Status is PaymentStatus.Cancelled or PaymentStatus.Failed && status.State is GatewayOrderState.PreAuthorized or GatewayOrderState.Deposited)
        {
            return await ChangeAsync(paymentId, p => p.FlagGatewayAttention(GatewayAttention.StatusMismatch, GatewayStatusInterpreter.Detail(status), now), cancellationToken);
        }

        return Result.Success();
    }

    private static string Amounts(string what, Money gateway, Money recorded) =>
        FormattableString.Invariant($"{what} {gateway:F} at the gateway, {recorded:F} recorded");

    /// <summary>
    /// Whether the return must read the status: anything not settled in a confirmed state yet, and an order closed as
    /// expired (the customer may have paid late on a page still open).
    /// </summary>
    private static bool NeedsStatusRead(Payment payment) =>
        payment.Status == PaymentStatus.Pending
        || payment.PendingGatewayOperation is not null
        || payment.GatewayAttention == GatewayAttention.UnknownStatus
        || (payment.Status == PaymentStatus.Cancelled && payment.ExpiredAtUtc is not null);

    private static string? LanguageOf(string? culture)
    {
        var cleaned = culture?.Trim();
        if (string.IsNullOrEmpty(cleaned))
        {
            return null;
        }

        var language = cleaned.Split('-', '_')[0].ToLowerInvariant();
        return language.Length == 2 && language.All(char.IsAsciiLetterLower) ? language : null;
    }

    private async Task<Result<OnlinePaymentRedirect>> StartAsync(OnlinePaymentRequest request, OnlinePaymentFlow flow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureOutsideUnitOfWork();
        if (!gateway.IsEnabled)
        {
            return PaymentsErrors.GatewayDisabled;
        }

        var amount = PaymentInput.Amount(request.Amount);
        if (amount.IsFailure)
        {
            return amount.Error;
        }

        var description = request.Description?.Trim();
        if (description is { Length: > Payment.GatewayDescriptionMaxLength })
        {
            return PaymentsErrors.OnlineDescriptionTooLong;
        }

        if (!await referential.IsActiveAgencyAsync(request.AgencyId, cancellationToken))
        {
            return PaymentsErrors.AgencyNotFound;
        }

        // A payment page or link only for a rental file of the agency of the payment (ADR-0067).
        var booking = await bookings.EnsureBookingOfAsync(request.BookingId, request.AgencyId, cancellationToken);
        if (booking.IsFailure)
        {
            return booking.Error;
        }

        var now = timeProvider.GetUtcNow();
        var returnPath = request.ReturnPath?.Trim() ?? string.Empty;
        var failPath = request.FailPath?.Trim() ?? string.Empty;
        if (!request.IsLink)
        {
            var since = now - AttemptReuseWindow;
            var reusable = await OnlineQuery()
                .Where(p => p.BookingId == request.BookingId && p.AgencyId == request.AgencyId && p.OnlineFlow == flow && !p.IsPaymentLink
                            && p.Status == PaymentStatus.Pending && p.PendingGatewayOperation == null && p.GatewayFormUrl != null
                            && p.RecordedAtUtc >= since && p.OnlineExpiresAtUtc > now
                            && p.GatewayReturnPath == returnPath && p.GatewayFailPath == failPath)
                .OrderByDescending(p => p.RecordedAtUtc)
                .ToListAsync(cancellationToken);
            if (reusable.FirstOrDefault(p => p.Amount == amount.Value) is { } previous)
            {
                return new OnlinePaymentRedirect(previous.Id, previous.OrderNumber, previous.GatewayFormUrl!, previous.OnlineExpiresAtUtc!.Value, Reused: true);
            }
        }

        var validity = request.Validity
            ?? await settings.GetAsync(request.IsLink ? PaymentSettings.PaymentLinkValidity : PaymentSettings.OnlineAttemptValidity, request.AgencyId, cancellationToken);
        var created = await unitOfWork.ExecuteGuardedAsync<Payment>(
            ct =>
            {
                var started = Payment.StartOnline(
                    new OnlinePaymentDetails(request.AgencyId, request.BookingId, amount.Value, flow, returnPath, failPath, now + validity, request.IsLink),
                    Actor,
                    now);
                if (started.IsFailure)
                {
                    return Task.FromResult<Result<Payment>>(started.Error);
                }

                db.Set<Payment>().Add(started.Value);
                return Task.FromResult<Result<Payment>>(started.Value);
            },
            cancellationToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var payment = created.Value;
        var registration = new GatewayRegistration(payment.OrderNumber, payment.Amount, LanguageOf(request.Culture), description, request.IsLink ? validity : null);
        var registered = flow == OnlinePaymentFlow.PreAuthorization
            ? await gateway.RegisterPreAuthAsync(registration, cancellationToken)
            : await gateway.RegisterAsync(registration, cancellationToken);
        if (registered.IsSuccess)
        {
            var order = registered.Value;
            var recorded = await ChangeAsync(payment.Id, p => p.RecordGatewayRegistration(order.GatewayOrderId, order.FormUrl, timeProvider.GetUtcNow()), cancellationToken);
            return recorded.IsFailure
                ? recorded.Error
                : new OnlinePaymentRedirect(payment.Id, payment.OrderNumber, order.FormUrl, payment.OnlineExpiresAtUtc!.Value, Reused: false);
        }

        if (registered.Error == PaymentsErrors.GatewayRejected || registered.Error == PaymentsErrors.GatewayAmountNotRepresentable)
        {
            // The gateway refused the order server to server: the attempt is closed as failed.
            await ApplyConfirmedAsync(payment.Id, new ConfirmedGatewayStatus(GatewayPaymentState.Declined, Detail: $"registration refused ({registered.Error.Code})"), cancellationToken);
        }
        else
        {
            // No usable answer: the order may exist, but no payment page was handed out; read it for the record. The
            // attempt cannot be paid and expires at the end of its validity.
            await RefreshCoreAsync(payment.Id, cancellationToken);
        }

        return registered.Error;
    }

    /// <summary>Marks the capture before the call: restarts a capture left without answer once its delay is over.</summary>
    private async Task<Result<Money>> MarkCaptureAsync(Payment payment, Money target, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (payment.PendingGatewayOperation == PendingGatewayOperation.Capture)
        {
            if (payment.PendingGatewayAcknowledged)
            {
                return PaymentsErrors.OnlineCaptureUnconfirmed;
            }

            if (payment.PendingGatewaySinceUtc is { } since && now - since < OperationGrace)
            {
                return PaymentsErrors.OnlineOperationInProgress;
            }

            // The previous deposit had no effect (the status read just showed the payment still pre-authorized).
            var abandoned = await ChangeAsync(payment.Id, p => p.AbandonGatewayOperation(PendingGatewayOperation.Capture, null, null, now), cancellationToken);
            if (abandoned.IsFailure)
            {
                return abandoned.Error;
            }
        }
        else if (payment.PendingGatewayOperation is not null)
        {
            return PaymentsErrors.OnlineOperationInProgress;
        }

        var marked = await ChangeAsync(payment.Id, p => p.RequestGatewayCapture(target, now), cancellationToken);
        return marked.IsFailure ? marked.Error : target;
    }

    /// <summary>
    /// The release of a payment from a caller: when its status could not be read, only a release of a payment not made
    /// yet is recorded; nothing is sent to the gateway on the faith of a state that may be out of date.
    /// </summary>
    private Task<Result<Payment>> ReleaseAsync(Snapshot snapshot, CancellationToken cancellationToken) =>
        snapshot.ReadError is { } readError && snapshot.Payment.Status != PaymentStatus.Pending
            ? Task.FromResult<Result<Payment>>(readError)
            : ReverseCoreAsync(snapshot.Payment, cancellationToken);

    /// <summary>The release of one payment: recorded, sent when pre-authorized, then confirmed by a status read.</summary>
    private async Task<Result<Payment>> ReverseCoreAsync(Payment payment, CancellationToken cancellationToken)
    {
        if (payment.Status is PaymentStatus.Cancelled or PaymentStatus.Failed)
        {
            return payment;
        }

        if (payment.Status is PaymentStatus.Captured or PaymentStatus.Refunded)
        {
            return PaymentsErrors.OnlineAlreadyCaptured;
        }

        var now = timeProvider.GetUtcNow();
        if (payment.Status == PaymentStatus.Pending)
        {
            // Not paid yet: the release waits for a pre-authorization (catch-up job); an unpaid order expires.
            var recorded = await ChangeAsync(payment.Id, p => p.RequestGatewayReverse(now), cancellationToken);
            return recorded.IsFailure ? recorded.Error : await ReloadAsync(payment.Id, cancellationToken);
        }

        if (payment.ExternalReference is null)
        {
            return PaymentsErrors.OnlinePaymentNotFound;
        }

        switch (payment.PendingGatewayOperation, payment.PendingGatewayAcknowledged)
        {
            case (PendingGatewayOperation.Reverse, true):
                return PaymentsErrors.OnlineReversalUnconfirmed;
            case (PendingGatewayOperation.Reverse, false) when payment.PendingGatewaySinceUtc is { } since && now - since < OperationGrace && since > payment.StatusChangedAtUtc:
                return PaymentsErrors.OnlineOperationInProgress;
            case (PendingGatewayOperation.Reverse, false):
                var restarted = await ChangeAsync(payment.Id, p => p.RestartGatewayOperation(now), cancellationToken);
                if (restarted.IsFailure)
                {
                    return restarted.Error;
                }

                break;
            case (null, _):
                var requested = await ChangeAsync(payment.Id, p => p.RequestGatewayReverse(now), cancellationToken);
                if (requested.IsFailure)
                {
                    return requested.Error;
                }

                break;
            default:
                return PaymentsErrors.OnlineOperationInProgress;
        }

        var reverse = await gateway.ReverseAsync(new GatewayOrderReference(payment.OrderNumber, payment.ExternalReference), cancellationToken);
        if (reverse.IsSuccess)
        {
            await ChangeAsync(payment.Id, p => p.AcknowledgeGatewayOperation(PendingGatewayOperation.Reverse), cancellationToken);
        }
        else if (reverse.Error == PaymentsErrors.GatewayRejected)
        {
            await ChangeAsync(
                payment.Id,
                p => p.AbandonGatewayOperation(PendingGatewayOperation.Reverse, GatewayAttention.OperationRejected, "reverse.do refused", timeProvider.GetUtcNow()),
                cancellationToken);
        }

        var refreshed = await RefreshCoreAsync(payment.Id, cancellationToken);
        if (refreshed.IsSuccess && refreshed.Value.Status == PaymentStatus.Cancelled)
        {
            return refreshed.Value;
        }

        return reverse.IsSuccess ? PaymentsErrors.OnlineReversalUnconfirmed : reverse.Error;
    }

    /// <summary>Sends an approved online refund to the gateway, then confirms it from the answer and a status read.</summary>
    private async Task<Result<OnlineRefundOutcome>> SendRefundAsync(Guid refundId, CancellationToken cancellationToken)
    {
        var refund = await db.Set<Payment>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == refundId, cancellationToken);
        if (refund?.OriginalPaymentId is not { } originalId)
        {
            return PaymentsErrors.PaymentNotFound;
        }

        var original = await LoadAsync(originalId, cancellationToken);
        if (original?.ExternalReference is not { } gatewayOrderId)
        {
            return PaymentsErrors.OnlinePaymentNotFound;
        }

        var sent = await gateway.RefundAsync(new GatewayOrderOperation(original.OrderNumber, gatewayOrderId, refund.Amount, refund.Id), cancellationToken);
        if (sent.IsSuccess)
        {
            await ChangeAsync(refundId, r => r.AcknowledgeGatewayOperation(PendingGatewayOperation.Refund), cancellationToken);
        }
        else if (sent.Error == PaymentsErrors.GatewayRejected)
        {
            await ChangeAsync<Guid>(
                async ct =>
                {
                    var paidBack = await db.FindPaymentAsync(refundId, ct);
                    var payment = await db.FindPaymentAsync(originalId, ct);
                    if (paidBack is null || payment is null)
                    {
                        return PaymentsErrors.PaymentNotFound;
                    }

                    var now = timeProvider.GetUtcNow();
                    var released = payment.ReleaseOnlineRefund(paidBack, PaymentStatus.Failed, "refund.do refused by the payment gateway", Actor, now);
                    if (released.IsFailure)
                    {
                        return released.Error;
                    }

                    payment.FlagGatewayAttention(GatewayAttention.OperationRejected, "refund.do refused", now);
                    return paidBack.Id;
                },
                cancellationToken);
        }

        // Without a clear answer the refund stays sent (its amount reserved): the next status reads confirm it.
        await RefreshCoreAsync(originalId, cancellationToken);
        var after = await db.Set<Payment>().AsNoTracking().FirstAsync(p => p.Id == refundId, cancellationToken);
        var state = OnlinePaymentMapping.RefundStateOf(after);
        return state == OnlineRefundStateInfo.Failed ? PaymentsErrors.GatewayRejected : new OnlineRefundOutcome(refundId, originalId, state);
    }

    /// <summary>
    /// Reads the status of the payment from the gateway and applies it. An order the gateway cannot report on, past its
    /// validity and never handed to the customer (no payment page recorded), is closed as expired: nobody could pay it.
    /// </summary>
    private async Task<Result<Payment>> RefreshCoreAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var snapshot = await LoadAsync(paymentId, cancellationToken);
        if (snapshot is null)
        {
            return PaymentsErrors.OnlinePaymentNotFound;
        }

        var read = await gateway.GetOrderStatusExtendedAsync(new GatewayOrderReference(snapshot.OrderNumber, snapshot.ExternalReference), cancellationToken);
        if (read.IsSuccess)
        {
            return await ApplyReadAsync(paymentId, read.Value, cancellationToken);
        }

        if (snapshot.GatewayFormUrl is null && GatewayStatusInterpreter.IsOver(snapshot, timeProvider.GetUtcNow()))
        {
            await ApplyConfirmedAsync(
                paymentId,
                new ConfirmedGatewayStatus(GatewayPaymentState.Expired, Detail: $"registration never confirmed ({read.Error.Code})"),
                cancellationToken);
        }

        return read.Error;
    }

    /// <summary>Applies a status read in a unit of work (retried on a concurrent change): status, refunds, flags.</summary>
    private Task<Result<Payment>> ApplyReadAsync(Guid paymentId, GatewayOrderStatus status, CancellationToken cancellationToken) =>
        ChangeAsync<Payment>(
            async ct =>
            {
                var payment = await db.FindPaymentAsync(paymentId, ct);
                if (payment is null || !payment.IsOnline || payment.Direction != PaymentDirection.Incoming)
                {
                    return PaymentsErrors.OnlinePaymentNotFound;
                }

                var now = timeProvider.GetUtcNow();
                var before = payment.Status;
                payment.RecordGatewayStatusCode(status.StatusCode);

                // A pre-authorization debited without any capture of ours and without the debited amount (outside the
                // platform, or a capture given up): the order amount is assumed, and flagged.
                var assumedAmount = status.State is GatewayOrderState.Deposited or GatewayOrderState.Refunded
                                    && status.DepositedAmount is null
                                    && payment.OnlineFlow == OnlinePaymentFlow.PreAuthorization
                                    && payment.Status is not (PaymentStatus.Captured or PaymentStatus.Refunded)
                                    && payment.PendingGatewayOperation != PendingGatewayOperation.Capture;
                var confirmed = GatewayStatusInterpreter.Interpret(payment, status, now);
                if (confirmed is null)
                {
                    if (payment.Status is PaymentStatus.Pending or PaymentStatus.PreAuthorized || payment.PendingGatewayOperation is not null)
                    {
                        payment.FlagGatewayAttention(GatewayAttention.UnknownStatus, GatewayStatusInterpreter.Detail(status), now);
                    }
                }
                else
                {
                    var applied = payment.ApplyConfirmedGatewayStatus(confirmed, Actor, now);
                    if (applied.IsFailure)
                    {
                        var attention = applied.Error == PaymentsErrors.GatewayAmountInvalid ? GatewayAttention.AmountMismatch : GatewayAttention.StatusMismatch;
                        payment.FlagGatewayAttention(attention, $"{GatewayStatusInterpreter.Detail(status)}; recorded {payment.Status}", now);
                    }
                    else if (assumedAmount)
                    {
                        payment.FlagGatewayAttention(GatewayAttention.AmountMismatch, $"{GatewayStatusInterpreter.Detail(status)}; debited amount not reported, order amount assumed", now);
                    }
                    else
                    {
                        ClearResolvedAttention(payment);
                    }
                }

                await ConfirmSentRefundsAsync(payment, status, now, ct);
                PublishStatusChange(payment, before);
                return payment;
            },
            cancellationToken);

    /// <summary>Applies a status decided from a server-to-server answer other than a status read (registration refused, expiry).</summary>
    private Task<Result<Payment>> ApplyConfirmedAsync(Guid paymentId, ConfirmedGatewayStatus confirmed, CancellationToken cancellationToken) =>
        ChangeAsync<Payment>(
            async ct =>
            {
                var payment = await db.FindPaymentAsync(paymentId, ct);
                if (payment is null)
                {
                    return PaymentsErrors.OnlinePaymentNotFound;
                }

                var before = payment.Status;
                var applied = payment.ApplyConfirmedGatewayStatus(confirmed, Actor, timeProvider.GetUtcNow());
                if (applied.IsFailure)
                {
                    return applied.Error;
                }

                PublishStatusChange(payment, before);
                return payment;
            },
            cancellationToken);

    /// <summary>
    /// Tells the other modules about a confirmed change of status (contract message, written with the change; a replayed
    /// transaction writes it again on its clean change tracker, a no-op replay of the same status writes nothing).
    /// </summary>
    private void PublishStatusChange(Payment payment, PaymentStatus before)
    {
        if (payment.Status == before || payment.Direction != PaymentDirection.Incoming || payment.OnlineFlow is not { } flow)
        {
            return;
        }

        var expired = payment.ExpiredAtUtc is not null;
        outbox.Enqueue(new OnlinePaymentStatusChanged(
            payment.Id,
            payment.BookingId,
            payment.AgencyId,
            flow.ToInfo(),
            payment.IsPaymentLink,
            before.ToInfo(),
            payment.Status.ToInfo(),
            Expired: expired && payment.Status == PaymentStatus.Cancelled,
            PaidLate: expired && before == PaymentStatus.Cancelled));
    }

    /// <summary>
    /// Confirms the refunds sent for this payment: up to the refunded amount the gateway reports when it does (TODO(spec)
    /// field), else the refunds it acknowledged — unless the order is in a state where no refund is possible.
    /// </summary>
    private async Task ConfirmSentRefundsAsync(Payment original, GatewayOrderStatus status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (original.Status is not (PaymentStatus.Captured or PaymentStatus.Refunded) || original.PendingRefundAmount.IsZero)
        {
            return;
        }

        var sent = await db.Set<Payment>()
            .Where(r => r.OriginalPaymentId == original.Id && r.Method == PaymentMethod.ClicToPay && r.Direction == PaymentDirection.Refund
                        && r.Status == PaymentStatus.Pending && r.PendingGatewayOperation == PendingGatewayOperation.Refund)
            .ToListAsync(cancellationToken);
        if (sent.Count == 0)
        {
            return;
        }

        if (status.State is GatewayOrderState.Registered or GatewayOrderState.PreAuthorized or GatewayOrderState.Reversed or GatewayOrderState.Declined)
        {
            original.FlagGatewayAttention(GatewayAttention.RefundNotConfirmed, GatewayStatusInterpreter.Detail(status), now);
            return;
        }

        var ordered = sent.OrderBy(r => r.PendingGatewaySinceUtc).ThenBy(r => r.Id).ToList();
        if (status.RefundedAmount is { } refunded)
        {
            foreach (var refund in ordered.TakeWhile(r => refunded >= original.RefundedAmount + r.Amount))
            {
                original.ConfirmOnlineRefund(refund, Actor, now);
            }

            return;
        }

        var fullyRefunded = status.State == GatewayOrderState.Refunded
                            && original.RefundedAmount + ordered.Select(r => r.Amount).Sum() == original.CapturedAmount;
        foreach (var refund in ordered.Where(r => fullyRefunded || r.PendingGatewayAcknowledged))
        {
            original.ConfirmOnlineRefund(refund, Actor, now);
        }
    }

    /// <summary>Flags resolved by a consistent status: an unknown status that became known, an operation now done.</summary>
    private static void ClearResolvedAttention(Payment payment)
    {
        var resolved = payment.GatewayAttention switch
        {
            GatewayAttention.UnknownStatus => true,
            GatewayAttention.CaptureNotConfirmed => payment.Status is PaymentStatus.Captured or PaymentStatus.Refunded,
            GatewayAttention.ReversalNotConfirmed => payment.Status == PaymentStatus.Cancelled,
            _ => false,
        };
        if (resolved)
        {
            payment.ClearGatewayAttention();
        }
    }

    /// <summary>
    /// The payment after a status read when something is unresolved (re-running after a time-out reads first). When that
    /// read fails, the snapshot carries its error: the caller must not send anything on the faith of a stale state.
    /// </summary>
    private async Task<Result<Snapshot>> CurrentAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await LoadAsync(paymentId, cancellationToken);
        if (payment is null)
        {
            return PaymentsErrors.OnlinePaymentNotFound;
        }

        if (payment.Status != PaymentStatus.Pending && payment.PendingGatewayOperation is null)
        {
            return new Snapshot(payment, null);
        }

        var refreshed = await RefreshCoreAsync(paymentId, cancellationToken);
        return refreshed.IsSuccess ? new Snapshot(refreshed.Value, null) : new Snapshot(payment, refreshed.Error);
    }

    private async Task<Result<OnlinePaymentInfo>> InfoAsync(Payment payment, CancellationToken cancellationToken)
    {
        var refunds = await OnlineRefunds([payment.Id]).ToListAsync(cancellationToken);
        return payment.ToOnlineInfo(refunds);
    }

    /// <summary>Online incoming payments, read without tracking.</summary>
    private IQueryable<Payment> OnlineQuery() =>
        db.Set<Payment>().AsNoTracking().Where(p => p.Method == PaymentMethod.ClicToPay && p.Direction == PaymentDirection.Incoming);

    private IQueryable<Payment> OnlineRefunds(IReadOnlyCollection<Guid> paymentIds) =>
        db.Set<Payment>().AsNoTracking().Where(r => r.Method == PaymentMethod.ClicToPay && r.Direction == PaymentDirection.Refund
                                                    && r.OriginalPaymentId != null && paymentIds.Contains(r.OriginalPaymentId.Value));

    private Task<Payment?> LoadAsync(Guid paymentId, CancellationToken cancellationToken) =>
        OnlineQuery().FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

    private async Task<Result<Payment>> ReloadAsync(Guid paymentId, CancellationToken cancellationToken) =>
        await LoadAsync(paymentId, cancellationToken) is { } payment ? payment : PaymentsErrors.OnlinePaymentNotFound;

    /// <summary>Finds the payment of a return: by our order number, else by the gateway order id; the query proves nothing else.</summary>
    private async Task<Payment?> FindByOrderAsync(string? orderNumber, string? gatewayOrderId, CancellationToken cancellationToken)
    {
        var urlOrderId = gatewayOrderId?.Trim();
        if (orderNumber is { } number && Guid.TryParseExact(number.Trim(), "N", out var paymentId))
        {
            var payment = await LoadAsync(paymentId, cancellationToken);
            if (payment is not null)
            {
                if (urlOrderId is { Length: > 0 } && payment.ExternalReference is { } known && !string.Equals(known, urlOrderId, StringComparison.Ordinal))
                {
                    LogReturnMismatch(payment.Id);
                }

                return payment;
            }
        }

        if (urlOrderId is not { Length: > 0 and <= Payment.ReferenceMaxLength })
        {
            return null;
        }

        return await OnlineQuery().FirstOrDefaultAsync(p => p.ExternalReference == urlOrderId, cancellationToken);
    }

    /// <summary>Runs a change of one payment in a unit of work, retried when another request changed it meanwhile.</summary>
    private Task<Result> ChangeAsync(Guid paymentId, Func<Payment, Result> change, CancellationToken cancellationToken) =>
        RetryOnConflictAsync(
            () => unitOfWork.ExecuteGuardedAsync(
                async ct =>
                {
                    var payment = await db.FindPaymentAsync(paymentId, ct);
                    return payment is null ? PaymentsErrors.PaymentNotFound : change(payment);
                },
                cancellationToken));

    private Task<Result<T>> ChangeAsync<T>(Func<CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken) =>
        RetryOnConflictAsync(() => unitOfWork.ExecuteGuardedAsync(operation, cancellationToken));

    private static async Task<TResult> RetryOnConflictAsync<TResult>(Func<Task<TResult>> attempt)
        where TResult : Result
    {
        for (var tries = 1; ; tries++)
        {
            var result = await attempt();
            if (result.IsSuccess || result.Error != PaymentsErrors.ConcurrentUpdate || tries >= ConcurrencyAttempts)
            {
                return result;
            }
        }
    }

    private void EnsureOutsideUnitOfWork()
    {
        if (unitOfWork.HasActiveTransaction)
        {
            throw new InvalidOperationException(
                "Online payment operations call the payment gateway and must not run inside a unit of work: commit first, then call them.");
        }
    }

    /// <summary>A payment as read, with the error of the status read that should have refreshed it, if it failed.</summary>
    private sealed record Snapshot(Payment Payment, Error? ReadError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Return from the payment page for payment {PaymentId} with a gateway order id that does not match the recorded one; the recorded order is used.")]
    private partial void LogReturnMismatch(Guid paymentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Release of online payment {PaymentId} deferred to the catch-up job ({ErrorCode}).")]
    private partial void LogReleaseDeferred(Guid paymentId, string errorCode);
}
