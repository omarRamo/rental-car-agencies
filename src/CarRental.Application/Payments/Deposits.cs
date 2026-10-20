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
/// Security deposits (<see cref="IDeposits"/>). A cash deposit is received into, and returned or retained from, the open
/// cash session of the acting user through <see cref="ICashDrawer"/> (deposit cash), in the same unit of work.
/// </summary>
internal sealed class Deposits(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IUserContext userContext,
    ICashDrawer cashDrawer,
    IReferentialQueries referential,
    TimeProvider timeProvider) : IDeposits
{
    private bool CanManage => userContext.HasPermission(Permissions.PaymentsManage);

    private bool CanRead => userContext.HasPermission(Permissions.PaymentsRead);

    public async Task<Result<Guid>> TakeAsync(DepositRequest request, CancellationToken cancellationToken = default)
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

        var mode = request.Mode.ToDomain();
        return await unitOfWork.ExecuteGuardedAsync<Guid>(
            async ct =>
            {
                Guid? sessionId = null;
                if (mode == DepositMode.Cash)
                {
                    var session = await cashDrawer.GetOpenSessionIdAsync(request.AgencyId, ct);
                    if (session.IsFailure)
                    {
                        return session.Error;
                    }

                    sessionId = session.Value;
                }

                var taken = DepositHold.Take(
                    new DepositDetails(request.AgencyId, request.BookingId, mode, amount.Value, request.ExternalReference, request.Notes, sessionId),
                    userContext.ToAuditActor(),
                    timeProvider.GetUtcNow());
                if (taken.IsFailure)
                {
                    return taken.Error;
                }

                var deposit = taken.Value;
                if (sessionId is { } cashSessionId)
                {
                    var moved = await cashDrawer.RecordDepositReceivedAsync(new CashDepositEntry(cashSessionId, deposit.Id, deposit.BookingId, deposit.Amount), ct);
                    if (moved.IsFailure)
                    {
                        return moved.Error;
                    }
                }

                db.Set<DepositHold>().Add(deposit);
                return deposit.Id;
            },
            cancellationToken);
    }

    public Task<Result> ReleaseAsync(Guid depositId, CancellationToken cancellationToken = default) =>
        SettleAsync(depositId, (deposit, sessionId, actor, now) => deposit.Release(sessionId, actor, now), cancellationToken);

    public async Task<Result> RetainAsync(Guid depositId, DepositRetentionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanManage)
        {
            return PaymentsErrors.Forbidden;
        }

        var amount = Money.FromExact(request.Amount, Currency.Tnd);
        if (amount.IsFailure)
        {
            return PaymentsErrors.DepositRetainedAmountInvalid;
        }

        return await SettleAsync(
            depositId,
            (deposit, sessionId, actor, now) => deposit.Retain(amount.Value, request.Justification, request.AttachmentReference, sessionId, actor, now),
            cancellationToken);
    }

    public async Task<Result<BookingDeposits>> GetBookingDepositsAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!CanRead)
        {
            return PaymentsErrors.Forbidden;
        }

        var deposits = await db.Set<DepositHold>()
            .AsNoTracking()
            .Where(d => d.BookingId == bookingId)
            .OrderBy(d => d.TakenAtUtc)
            .ThenBy(d => d.Id)
            .ToListAsync(cancellationToken);
        return new BookingDeposits(bookingId, [.. deposits.Select(d => d.ToInfo())], PaymentMapping.TotalsOf(deposits.Select(d => d.AmountsOf())));
    }

    /// <summary>
    /// Releases or retains a held deposit; a cash deposit is settled from the open cash session of the acting user (the
    /// drawer brings in a deposit received in another session first).
    /// </summary>
    private async Task<Result> SettleAsync(
        Guid depositId,
        Func<DepositHold, Guid?, AuditActor, DateTimeOffset, Result> settle,
        CancellationToken cancellationToken)
    {
        if (!CanManage)
        {
            return PaymentsErrors.Forbidden;
        }

        return await unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var deposit = await db.FindDepositAsync(depositId, ct);
                if (deposit is null)
                {
                    return PaymentsErrors.DepositNotFound;
                }

                if (!deposit.IsHeld)
                {
                    return PaymentsErrors.DepositAlreadySettled;
                }

                Guid? sessionId = null;
                if (deposit.Mode == DepositMode.Cash)
                {
                    var session = await cashDrawer.GetOpenSessionIdAsync(deposit.AgencyId, ct);
                    if (session.IsFailure)
                    {
                        return session.Error;
                    }

                    sessionId = session.Value;
                }

                var settled = settle(deposit, sessionId, userContext.ToAuditActor(), timeProvider.GetUtcNow());
                if (settled.IsFailure || sessionId is not { } cashSessionId)
                {
                    return settled;
                }

                return await cashDrawer.RecordDepositSettledAsync(
                    new CashDepositSettlementEntry(
                        cashSessionId,
                        deposit.Id,
                        deposit.BookingId,
                        deposit.Amount,
                        deposit.RetainedAmount,
                        deposit.TakenInCashSessionId ?? cashSessionId),
                    ct);
            },
            cancellationToken);
    }
}
