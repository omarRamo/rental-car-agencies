using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Cash;

/// <summary>
/// Cash sessions of the back office (<c>/admin/cash</c>). <c>cash.own</c>: the acting user's own session (open,
/// movements, close, read); <c>cash.manage</c>: every session, lock and adjustments. Every change is audited
/// automatically and raises its domain events (outbox).
/// </summary>
internal sealed class CashSessionService(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IUserContext userContext,
    IReferentialQueries referential,
    IIdentityQueries identity,
    TimeProvider timeProvider) : ICashSessions
{
    private bool CanOwn => userContext.HasPermission(Permissions.CashOwn);

    private bool CanManage => userContext.HasPermission(Permissions.CashManage);

    public async Task<Result<Guid>> OpenAsync(OpenCashSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanOwn)
        {
            return CashErrors.Forbidden;
        }

        if (userContext.UserId is not { } cashierId)
        {
            return CashErrors.CashierRequired;
        }

        var openingFloat = CashInput.Countable(request.OpeningFloat, CashErrors.OpeningFloatInvalid);
        if (openingFloat.IsFailure)
        {
            return openingFloat.Error;
        }

        var agencies = await referential.GetAgenciesAsync(cancellationToken);
        if (!agencies.Any(a => a.Id == request.AgencyId && a.IsActive))
        {
            return CashErrors.AgencyNotFound;
        }

        var names = await identity.GetDisplayNamesAsync([cashierId], cancellationToken);
        var cashierName = names.GetValueOrDefault(cashierId) ?? userContext.UserName;
        try
        {
            return await unitOfWork.ExecuteGuardedAsync<Guid>(
                async ct =>
                {
                    var now = timeProvider.GetUtcNow();
                    var businessDate = CashSessionRules.BusinessDateOf(now);
                    var existing = await db.ExistingSessionsAsync(cashierId, request.AgencyId, businessDate, ct);
                    var allowed = CashSessionRules.EnsureCanOpen(existing, request.AgencyId, businessDate);
                    if (allowed.IsFailure)
                    {
                        return allowed.Error;
                    }

                    var opened = CashSession.Open(request.AgencyId, cashierId, cashierName, openingFloat.Value, now);
                    if (opened.IsFailure)
                    {
                        return opened.Error;
                    }

                    db.Set<CashSession>().Add(opened.Value);
                    return opened.Value.Id;
                },
                cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent opening for the same cashier won a unique index: report the rule it broke.
            var businessDate = CashSessionRules.BusinessDateOf(timeProvider.GetUtcNow());
            var existing = await db.ExistingSessionsAsync(cashierId, request.AgencyId, businessDate, cancellationToken);
            var allowed = CashSessionRules.EnsureCanOpen(existing, request.AgencyId, businessDate);
            if (allowed.IsFailure)
            {
                return allowed.Error;
            }

            throw;
        }
    }

    public async Task<Result<CashSessionDetails?>> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (!CanOwn)
        {
            return CashErrors.Forbidden;
        }

        if (userContext.UserId is not { } userId)
        {
            return Result.Success<CashSessionDetails?>(null);
        }

        var session = await db.Set<CashSession>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CashierId == userId && s.Status == CashSessionStatus.Open, cancellationToken);
        return Result.Success(session?.ToDetails());
    }

    public async Task<Result<CashSessionDetails>> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!CanOwn && !CanManage)
        {
            return CashErrors.Forbidden;
        }

        var session = await db.Set<CashSession>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return CashErrors.SessionNotFound;
        }

        return MayAccess(session) ? session.ToDetails() : CashErrors.SessionNotOwned;
    }

    public async Task<Result<Guid>> AddManualMovementAsync(Guid sessionId, ManualCashMovementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanOwn && !CanManage)
        {
            return CashErrors.Forbidden;
        }

        var amount = CashInput.Positive(request.Amount);
        if (amount.IsFailure)
        {
            return amount.Error;
        }

        return await ChangeSessionAsync<Guid>(
            sessionId,
            (session, actor, now) =>
            {
                var movement = session.AddManualMovement(request.Direction.ToDomain(), amount.Value, request.Reason, actor, now);
                return movement.IsSuccess ? movement.Value.Id : movement.Error;
            },
            cancellationToken);
    }

    public async Task<Result<Guid>> AddAdjustmentAsync(Guid sessionId, CashAdjustmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanManage)
        {
            return CashErrors.Forbidden;
        }

        var amount = CashInput.Signed(request.Amount);
        if (amount.IsFailure)
        {
            return amount.Error;
        }

        if (request.CorrectedSessionId is { } correctedId)
        {
            var corrected = await db.Set<CashSession>()
                .AsNoTracking()
                .Where(s => s.Id == correctedId)
                .Select(s => (CashSessionStatus?)s.Status)
                .FirstOrDefaultAsync(cancellationToken);
            if (corrected is null or CashSessionStatus.Open)
            {
                return CashErrors.CorrectedSessionInvalid;
            }
        }

        return await ChangeSessionAsync<Guid>(
            sessionId,
            (session, actor, now) =>
            {
                var movement = session.AddAdjustment(request.Pocket.ToDomain(), amount.Value, request.Reason, request.CorrectedSessionId, actor, now);
                return movement.IsSuccess ? movement.Value.Id : movement.Error;
            },
            cancellationToken);
    }

    public async Task<Result<CashSessionClosing>> CloseAsync(Guid sessionId, CloseCashSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanOwn && !CanManage)
        {
            return CashErrors.Forbidden;
        }

        var revenue = CashInput.Countable(request.CountedRevenueCash, CashErrors.CountedAmountInvalid);
        var deposit = CashInput.Countable(request.CountedDepositCash, CashErrors.CountedAmountInvalid);
        if (revenue.IsFailure || deposit.IsFailure)
        {
            return CashErrors.CountedAmountInvalid;
        }

        return await ChangeSessionAsync<CashSessionClosing>(
            sessionId,
            (session, actor, now) =>
            {
                var closed = session.Close(revenue.Value, deposit.Value, request.VarianceJustification, actor, now);
                return closed.IsSuccess
                    ? new CashSessionClosing(session.RevenueVariance!.Value, session.DepositVariance!.Value)
                    : Result.Failure<CashSessionClosing>(closed.Error);
            },
            cancellationToken);
    }

    public async Task<Result> LockAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return CashErrors.Forbidden;
        }

        var locked = await ChangeSessionAsync<bool>(
            sessionId,
            (session, actor, now) =>
            {
                var outcome = session.Lock(actor, now);
                return outcome.IsSuccess ? Result.Success(true) : Result.Failure<bool>(outcome.Error);
            },
            cancellationToken);
        return locked.IsSuccess ? Result.Success() : locked.Error;
    }

    public async Task<Result<CashSessionList>> ListAsync(CashSessionFilter filter, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        if (!CanOwn && !CanManage)
        {
            return CashErrors.Forbidden;
        }

        var query = db.Set<CashSession>().AsNoTracking();
        if (!CanManage)
        {
            var self = userContext.UserId ?? Guid.Empty;
            query = query.Where(s => s.CashierId == self);
        }
        else if (filter.CashierId is { } cashierId)
        {
            query = query.Where(s => s.CashierId == cashierId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(s => s.BusinessDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(s => s.BusinessDate <= to);
        }

        if (filter.AgencyId is { } agencyId)
        {
            query = query.Where(s => s.AgencyId == agencyId);
        }

        if (filter.Status is { } status)
        {
            var wanted = status.ToDomain();
            query = query.Where(s => s.Status == wanted);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(s => s.BusinessDate)
            .ThenBy(s => s.CashierName)
            .ThenBy(s => s.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(s => new CashSessionSummary(
                s.Id,
                s.AgencyId,
                s.CashierId,
                s.CashierName,
                s.BusinessDate,
                (CashSessionStatusInfo)(int)s.Status,
                s.OpeningFloat,
                s.ExpectedRevenueCash,
                s.ExpectedDepositCash,
                s.CountedRevenueCash,
                s.CountedDepositCash,
                s.RevenueVariance,
                s.DepositVariance,
                s.VarianceJustification,
                s.OpenedAtUtc,
                s.ClosedAtUtc,
                s.LockedAtUtc,
                s.LockedByName))
            .ToListAsync(cancellationToken);

        // Totals of the whole list, summed in memory (a few hundred sessions a year).
        var amounts = await query
            .Select(s => new SessionAmounts(
                s.OpeningFloat,
                s.ExpectedRevenueCash,
                s.ExpectedDepositCash,
                s.CountedRevenueCash,
                s.CountedDepositCash,
                s.RevenueVariance,
                s.DepositVariance))
            .ToListAsync(cancellationToken);
        var totals = new CashSessionListTotals(
            amounts.Count,
            amounts.Select(a => a.OpeningFloat).Sum(),
            amounts.Select(a => a.ExpectedRevenueCash).Sum(),
            amounts.Select(a => a.ExpectedDepositCash).Sum(),
            amounts.Select(a => a.CountedRevenueCash).OfType<Money>().Sum(),
            amounts.Select(a => a.CountedDepositCash).OfType<Money>().Sum(),
            amounts.Select(a => a.RevenueVariance).OfType<Money>().Sum(),
            amounts.Select(a => a.DepositVariance).OfType<Money>().Sum());

        return new CashSessionList(new PagedResult<CashSessionSummary>(items, total, page.Page, page.PageSize), totals);
    }

    public async Task<Result<IReadOnlyList<CashierInfo>>> ListCashiersAsync(CancellationToken cancellationToken = default)
    {
        if (!CanOwn && !CanManage)
        {
            return CashErrors.Forbidden;
        }

        var query = db.Set<CashSession>().AsNoTracking();
        if (!CanManage)
        {
            var self = userContext.UserId ?? Guid.Empty;
            query = query.Where(s => s.CashierId == self);
        }

        var cashiers = await query
            .GroupBy(s => s.CashierId)
            .Select(g => new { CashierId = g.Key, Name = g.Max(s => s.CashierName) })
            .ToListAsync(cancellationToken);
        return cashiers
            .Select(c => new CashierInfo(c.CashierId, c.Name ?? c.CashierId.ToString()))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private bool MayAccess(CashSession session) => CanManage || (CanOwn && session.CashierId == userContext.UserId);

    /// <summary>Loads the session inside a (replayable) unit of work, checks the access of the acting user and applies the change.</summary>
    private Task<Result<T>> ChangeSessionAsync<T>(
        Guid sessionId,
        Func<CashSession, AuditActor, DateTimeOffset, Result<T>> change,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteGuardedAsync<T>(
            async ct =>
            {
                var session = await db.LoadSessionAsync(sessionId, ct);
                if (session is null)
                {
                    return CashErrors.SessionNotFound;
                }

                return MayAccess(session) ? change(session, userContext.ToAuditActor(), timeProvider.GetUtcNow()) : CashErrors.SessionNotOwned;
            },
            cancellationToken);

    private sealed record SessionAmounts(
        Money OpeningFloat,
        Money ExpectedRevenueCash,
        Money ExpectedDepositCash,
        Money? CountedRevenueCash,
        Money? CountedDepositCash,
        Money? RevenueVariance,
        Money? DepositVariance);
}
