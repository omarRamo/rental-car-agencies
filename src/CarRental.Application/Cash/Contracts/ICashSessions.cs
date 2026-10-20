using CarRental.Application.Common.Paging;
using CarRental.Domain.Common;

namespace CarRental.Application.Cash.Contracts;

/// <summary>
/// Cash sessions (caisse, SPEC §8): one per agent, agency and Tunis day, opened with a float, closed by the agent with
/// the counted cash (variances justified), locked by a manager or the accountant. Rights (SPEC §3, "Clôture de caisse"):
/// <c>cash.own</c> gives the acting user their own session (open, movements, close, read); <c>cash.manage</c> (manager,
/// accountant) gives every session, the lock and the adjustments.
/// </summary>
public interface ICashSessions
{
    /// <summary>Opens the session of the acting user for today (Tunis) in an agency. Returns the session id.</summary>
    Task<Result<Guid>> OpenAsync(OpenCashSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>The open session of the acting user, with its movements; <see langword="null"/> when none is open.</summary>
    Task<Result<CashSessionDetails?>> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>A session with its movements: one's own with <c>cash.own</c>, any with <c>cash.manage</c>.</summary>
    Task<Result<CashSessionDetails>> GetAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Records other cash put in or taken from the revenue cash of an open session, with a reason. Returns the movement id.</summary>
    Task<Result<Guid>> AddManualMovementAsync(Guid sessionId, ManualCashMovementRequest request, CancellationToken cancellationToken = default);

    /// <summary>Corrects an earlier session through an adjustment in an open session (<c>cash.manage</c>). Returns the movement id.</summary>
    Task<Result<Guid>> AddAdjustmentAsync(Guid sessionId, CashAdjustmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Closes an open session with the counted cash; a variance needs a justification. Returns the variances.</summary>
    Task<Result<CashSessionClosing>> CloseAsync(Guid sessionId, CloseCashSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Locks a closed session (<c>cash.manage</c>): it never changes afterwards.</summary>
    Task<Result> LockAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sessions by business day (latest first) with the totals of the whole list; with <c>cash.own</c> only, the acting
    /// user's own sessions whatever the cashier filter.
    /// </summary>
    Task<Result<CashSessionList>> ListAsync(CashSessionFilter filter, PageRequest page, CancellationToken cancellationToken = default);

    /// <summary>Users who have had a session (filter of the list), by name; only oneself with <c>cash.own</c> alone.</summary>
    Task<Result<IReadOnlyList<CashierInfo>>> ListCashiersAsync(CancellationToken cancellationToken = default);
}
