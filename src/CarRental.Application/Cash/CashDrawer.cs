using CarRental.Application.Accounting.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Cash;

/// <summary>
/// The cash drawer of the acting user for the Payments module: cash payments, refunds and deposits go into the user's
/// own open session, inside the unit of work of the payment (one transaction for both). An agency without the cash module
/// (ADR-0067) has no drawer: its cash is refused with a clear reason, never left without a session.
/// </summary>
internal sealed class CashDrawer(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IUserContext userContext,
    TimeProvider timeProvider,
    IAccountingPeriods periods,
    IAgencyDirectory agencies) : ICashDrawer
{
    public async Task<Result<Guid>> GetOpenSessionIdAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.CashOwn))
        {
            return CashErrors.Forbidden;
        }

        if (!await agencies.HasModuleAsync(agencyId, AgencyModules.Cash, cancellationToken))
        {
            return CashErrors.ModuleOff;
        }

        if (userContext.UserId is not { } userId)
        {
            return CashErrors.NoOpenSession;
        }

        var session = await db.Set<CashSession>()
            .Where(s => s.CashierId == userId && s.Status == CashSessionStatus.Open)
            .Select(s => new { s.Id, s.AgencyId })
            .FirstOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return CashErrors.NoOpenSession;
        }

        return session.AgencyId == agencyId ? session.Id : CashErrors.SessionAgencyMismatch;
    }

    public Task<Result> RecordPaymentReceivedAsync(CashPaymentEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RecordAsync(
            entry.SessionId,
            (session, actor, now) => session.RecordPaymentReceived(entry.PaymentId, entry.BookingId, entry.Amount, actor, now),
            cancellationToken);
    }

    public Task<Result> RecordRefundPaidAsync(CashPaymentEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RecordAsync(
            entry.SessionId,
            (session, actor, now) => session.RecordRefundPaid(entry.PaymentId, entry.BookingId, entry.Amount, actor, now),
            cancellationToken);
    }

    public Task<Result> RecordDepositReceivedAsync(CashDepositEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RecordAsync(
            entry.SessionId,
            (session, actor, now) => session.RecordDepositReceived(entry.DepositId, entry.BookingId, entry.Amount, actor, now),
            cancellationToken);
    }

    public async Task<Result> RecordDepositSettledAsync(CashDepositSettlementEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureTransaction();
        var session = await LoadOwnSessionAsync(entry.SessionId, cancellationToken);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var actor = userContext.ToAuditActor();
        var now = timeProvider.GetUtcNow();
        var settled = session.Value.RecordDepositSettlement(
            new DepositCashSettlement(entry.DepositId, entry.BookingId, entry.Amount, entry.Retained, entry.ReceivedInSessionId),
            actor,
            now);
        if (settled.IsFailure)
        {
            return settled.Error;
        }

        if (entry.ReceivedInSessionId == entry.SessionId)
        {
            return Result.Success();
        }

        // The deposit leaves the drawer that received it when that session is still open (otherwise it comes from the safe).
        var origin = await db.LoadSessionAsync(entry.ReceivedInSessionId, cancellationToken);
        if (origin is not { IsOpen: true })
        {
            return Result.Success();
        }

        var handedOver = origin.RecordDepositHandedOver(entry.DepositId, entry.BookingId, entry.Amount, entry.SessionId, actor, now);
        return handedOver.IsSuccess ? Result.Success() : handedOver.Error;
    }

    private async Task<Result> RecordAsync(
        Guid sessionId,
        Func<CashSession, AuditActor, DateTimeOffset, Result<CashMovement>> record,
        CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var session = await LoadOwnSessionAsync(sessionId, cancellationToken);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var recorded = record(session.Value, userContext.ToAuditActor(), timeProvider.GetUtcNow());
        return recorded.IsSuccess ? Result.Success() : recorded.Error;
    }

    /// <summary>
    /// The tracked session, which must be the acting user's (cash always goes through one's own drawer) and whose business
    /// day must be in an open month: its movements are posted on that day (ADR-0065).
    /// </summary>
    private async Task<Result<CashSession>> LoadOwnSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!userContext.HasPermission(Permissions.CashOwn))
        {
            return CashErrors.Forbidden;
        }

        var session = await db.LoadSessionAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return CashErrors.SessionNotFound;
        }

        if (session.CashierId != userContext.UserId)
        {
            return CashErrors.SessionNotOwned;
        }

        var open = await periods.EnsureOpenAsync(session.AgencyId, session.BusinessDate, cancellationToken);
        return open.IsFailure ? open.Error : session;
    }

    private void EnsureTransaction()
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            throw new InvalidOperationException("Cash movements are recorded inside the unit of work of the payment or deposit that causes them.");
        }
    }
}
