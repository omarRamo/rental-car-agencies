using CarRental.Application.Accounting.Contracts;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Payments;

/// <summary>
/// Counter payments and refunds (<see cref="ICounterPayments"/>). Cash goes into the open cash session of the acting
/// user through <see cref="ICashDrawer"/>, in the same unit of work: the payment and its cash movement are saved together
/// or not at all. The operation is replayed whole after a transient failure, so everything is loaded inside it. A payment
/// is recorded for a rental file of its agency only (<see cref="PaymentBookings"/>, ADR-0067).
/// </summary>
internal sealed class CounterPayments(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IUserContext userContext,
    ICashDrawer cashDrawer,
    IReferentialQueries referential,
    IBookingsQueries bookings,
    TimeProvider timeProvider,
    IAccountingPeriods periods) : ICounterPayments
{
    private bool CanManage => userContext.HasPermission(Permissions.PaymentsManage);

    private bool CanRead => userContext.HasPermission(Permissions.PaymentsRead);

    public async Task<Result<Guid>> RecordPaymentAsync(CounterPaymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanManage)
        {
            return PaymentsErrors.Forbidden;
        }

        var amount = PaymentInput.Amount(request.Amount);
        if (amount.IsFailure)
        {
            return amount.Error;
        }

        if (!await referential.IsActiveAgencyAsync(request.AgencyId, cancellationToken))
        {
            return PaymentsErrors.AgencyNotFound;
        }

        var booking = await bookings.EnsureBookingOfAsync(request.BookingId, request.AgencyId, cancellationToken);
        if (booking.IsFailure)
        {
            return booking.Error;
        }

        var method = request.Method.ToDomain();
        return await unitOfWork.ExecuteGuardedAsync<Guid>(
            async ct =>
            {
                // Monthly closing (ADR-0065): no payment dated in a closed month.
                var open = await periods.EnsureOpenAsync(request.AgencyId, TunisTime.Today(timeProvider), ct);
                if (open.IsFailure)
                {
                    return open.Error;
                }

                Guid? sessionId = null;
                if (method == PaymentMethod.Cash)
                {
                    var session = await cashDrawer.GetOpenSessionIdAsync(request.AgencyId, ct);
                    if (session.IsFailure)
                    {
                        return session.Error;
                    }

                    sessionId = session.Value;
                }

                var details = new CounterPaymentDetails(
                    request.AgencyId,
                    request.BookingId,
                    method,
                    amount.Value,
                    request.ExternalReference,
                    request.Notes,
                    sessionId,
                    request.AwaitingConfirmation);
                var recorded = Payment.RecordCounter(details, userContext.ToAuditActor(), timeProvider.GetUtcNow());
                if (recorded.IsFailure)
                {
                    return recorded.Error;
                }

                var payment = recorded.Value;
                if (sessionId is { } cashSessionId)
                {
                    var moved = await cashDrawer.RecordPaymentReceivedAsync(new CashPaymentEntry(cashSessionId, payment.Id, payment.BookingId, payment.Amount), ct);
                    if (moved.IsFailure)
                    {
                        return moved.Error;
                    }
                }

                db.Set<Payment>().Add(payment);
                return payment.Id;
            },
            cancellationToken);
    }

    public async Task<Result<Guid>> RecordRefundAsync(CounterRefundRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanManage)
        {
            return PaymentsErrors.Forbidden;
        }

        var amount = PaymentInput.Amount(request.Amount);
        if (amount.IsFailure)
        {
            return amount.Error;
        }

        var method = request.Method.ToDomain();
        return await unitOfWork.ExecuteGuardedAsync<Guid>(
            async ct =>
            {
                var original = await db.FindPaymentAsync(request.PaymentId, ct);
                if (original is null)
                {
                    return PaymentsErrors.PaymentNotFound;
                }

                // Monthly closing (ADR-0065): no refund dated in a closed month.
                var open = await periods.EnsureOpenAsync(original.AgencyId, TunisTime.Today(timeProvider), ct);
                if (open.IsFailure)
                {
                    return open.Error;
                }

                Guid? sessionId = null;
                if (method == PaymentMethod.Cash)
                {
                    var session = await cashDrawer.GetOpenSessionIdAsync(original.AgencyId, ct);
                    if (session.IsFailure)
                    {
                        return session.Error;
                    }

                    sessionId = session.Value;
                }

                var refunded = original.RecordRefund(
                    new CounterRefundDetails(method, amount.Value, request.Reason, request.ExternalReference, sessionId),
                    userContext.ToAuditActor(),
                    timeProvider.GetUtcNow());
                if (refunded.IsFailure)
                {
                    return refunded.Error;
                }

                var refund = refunded.Value;
                if (sessionId is { } cashSessionId)
                {
                    var moved = await cashDrawer.RecordRefundPaidAsync(new CashPaymentEntry(cashSessionId, refund.Id, refund.BookingId, refund.Amount), ct);
                    if (moved.IsFailure)
                    {
                        return moved.Error;
                    }
                }

                db.Set<Payment>().Add(refund);
                return refund.Id;
            },
            cancellationToken);
    }

    public async Task<Result> ConfirmPendingAsync(Guid paymentId, string? externalReference = null, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return PaymentsErrors.Forbidden;
        }

        return await unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var payment = await db.FindPaymentAsync(paymentId, ct);
                if (payment is null)
                {
                    return PaymentsErrors.PaymentNotFound;
                }

                // Monthly closing (ADR-0065): the money confirmed now is not dated in a closed month.
                var open = await periods.EnsureOpenAsync(payment.AgencyId, TunisTime.Today(timeProvider), ct);
                return open.IsFailure ? open : payment.Confirm(externalReference, userContext.ToAuditActor(), timeProvider.GetUtcNow());
            },
            cancellationToken);
    }

    public Task<Result> CancelPendingAsync(Guid paymentId, string reason, CancellationToken cancellationToken = default) =>
        ChangeAsync(paymentId, (payment, actor, now) => payment.Cancel(reason, actor, now), cancellationToken);

    public async Task<Result<BookingPayments>> GetBookingPaymentsAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!CanRead)
        {
            return PaymentsErrors.Forbidden;
        }

        var payments = await db.Set<Payment>()
            .AsNoTracking()
            .Where(p => p.BookingId == bookingId)
            .OrderBy(p => p.RecordedAtUtc)
            .ThenBy(p => p.Id)
            .ToListAsync(cancellationToken);
        return new BookingPayments(bookingId, [.. payments.Select(p => p.ToInfo())], PaymentMapping.TotalsOf(payments.Select(p => p.AmountsOf())));
    }

    private async Task<Result> ChangeAsync(Guid paymentId, Func<Payment, AuditActor, DateTimeOffset, Result> change, CancellationToken cancellationToken)
    {
        if (!CanManage)
        {
            return PaymentsErrors.Forbidden;
        }

        return await unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var payment = await db.FindPaymentAsync(paymentId, ct);
                return payment is null ? PaymentsErrors.PaymentNotFound : change(payment, userContext.ToAuditActor(), timeProvider.GetUtcNow());
            },
            cancellationToken);
    }
}
