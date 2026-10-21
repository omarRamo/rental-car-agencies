using System.Globalization;
using CarRental.Application.Accounting.Journals;
using CarRental.Application.Accounting.Periods;
using CarRental.Application.Accounting.Reconciliation;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Accounting.Closing;

/// <summary>A point checked before closing a month: what it is, whether it blocks, what it concerns.</summary>
public sealed record ClosingCheckView(string Key, ClosingCheckKind Kind, ClosingCheckSeverity Severity, string Subject, DateOnly? Date, Money? Amount);

/// <summary>What closing a month would do: its status, the checks, the totals of its journals and what keeps them from being complete.</summary>
public sealed record ClosingPreview(
    Guid AgencyId,
    AccountingMonth Month,
    AccountingPeriodStatus Status,
    bool IsOver,
    IReadOnlyList<ClosingCheckView> Checks,
    IReadOnlyList<JournalTotalView> Totals,
    IReadOnlyList<string> MissingKeys,
    IReadOnlyList<string> Errors)
{
    public bool HasBlockingChecks => Checks.Any(c => c.Severity == ClosingCheckSeverity.Blocking);

    /// <summary>The keys of the confirmable points the accountant confirms by closing.</summary>
    public IReadOnlyList<string> ConfirmableKeys => [.. Checks.Where(c => c.Severity == ClosingCheckSeverity.Confirmable).Select(c => c.Key)];

    public bool IsBalanced => Totals.All(t => t.IsBalanced);

    public bool CanClose => Status == AccountingPeriodStatus.Open && IsOver && !HasBlockingChecks && MissingKeys.Count == 0 && Errors.Count == 0 && IsBalanced;
}

/// <summary>A month of an agency in the list of the closings.</summary>
public sealed record PeriodView(
    AccountingMonth Month,
    AccountingPeriodStatus Status,
    bool IsOver,
    DateTimeOffset? ClosedAtUtc,
    string? ClosedByName,
    DateTimeOffset? ReopenedAtUtc,
    string? ReopenedByName,
    string? ReopenReason,
    int ReopenCount,
    int? EntryCount,
    Money? TotalDebit,
    Money? TotalCredit);

/// <summary>
/// The monthly closing (SPEC §8 "clôture mensuelle qui verrouille la période", ADR-0065, permission <c>accounting.manage</c>):
/// once a month is over and its checks pass — every cash session locked and every reconciliation difference justified
/// (blocking); the files still to invoice and the e-invoices not accepted by TTN confirmed (confirmable) —, its journals are
/// generated, checked balanced and frozen, and the month is locked: <see cref="Contracts.IAccountingPeriods"/> then refuses
/// every financial fact dated in it. Only a manager reopens it (<c>accounting.reopen</c>), with a reason; both are audited.
/// The closing and the reopening hold the exclusive lock of the month: the writers in progress finish first.
/// </summary>
public sealed class MonthlyClosingService
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _user;
    private readonly IAccountingPeriodLock _locks;
    private readonly IReferentialQueries _referential;
    private readonly ICashQueries _cash;
    private readonly IBillingQueries _billing;
    private readonly IBillingAccountingQueries _billingAccounting;
    private readonly JournalService _journals;
    private readonly ReconciliationService _reconciliation;
    private readonly IAuditTrail _audit;
    private readonly TimeProvider _clock;

    internal MonthlyClosingService(
        IAppDbContext db,
        IUnitOfWork unitOfWork,
        IUserContext user,
        IAccountingPeriodLock locks,
        IReferentialQueries referential,
        ICashQueries cash,
        IBillingQueries billing,
        IBillingAccountingQueries billingAccounting,
        JournalService journals,
        ReconciliationService reconciliation,
        IAuditTrail audit,
        TimeProvider clock)
    {
        _db = db;
        _unitOfWork = unitOfWork;
        _user = user;
        _locks = locks;
        _referential = referential;
        _cash = cash;
        _billing = billing;
        _billingAccounting = billingAccounting;
        _journals = journals;
        _reconciliation = reconciliation;
        _audit = audit;
        _clock = clock;
    }

    private bool CanManage => _user.HasPermission(Permissions.AccountingManage);

    private DateOnly Today => TunisTime.Today(_clock);

    /// <summary>The last <paramref name="count"/> months of the agency up to the current one (latest first), with their closing.</summary>
    public async Task<Result<IReadOnlyList<PeriodView>>> ListPeriodsAsync(Guid agencyId, int count = 12, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        var current = AccountingMonth.Of(Today);
        var first = current;
        for (var i = 1; i < Math.Clamp(count, 1, 60); i++)
        {
            first = first.Previous();
        }

        var periods = await _db.Set<AccountingPeriod>().AsNoTracking().Where(p => p.AgencyId == agencyId).ToListAsync(cancellationToken);
        var closingIds = periods.Where(p => p.CurrentClosingId is not null).Select(p => p.CurrentClosingId!.Value).ToList();
        var closings = await _db.Set<PeriodClosing>()
            .AsNoTracking()
            .IgnoreAutoIncludes()
            .Where(c => closingIds.Contains(c.Id))
            .Select(c => new { c.Id, c.EntryCount, c.TotalDebit, c.TotalCredit })
            .ToDictionaryAsync(c => c.Id, cancellationToken);
        var months = new List<PeriodView>();
        for (var month = current; month >= first; month = month.Previous())
        {
            var period = periods.FirstOrDefault(p => p.Year == month.Year && p.Month == month.Month);
            var closing = period?.CurrentClosingId is { } id ? closings.GetValueOrDefault(id) : null;
            months.Add(new PeriodView(
                month,
                period?.Status ?? AccountingPeriodStatus.Open,
                month.IsOverOn(Today),
                period?.ClosedAtUtc,
                period?.ClosedByName,
                period?.ReopenedAtUtc,
                period?.ReopenedByName,
                period?.ReopenReason,
                period?.ReopenCount ?? 0,
                closing?.EntryCount,
                closing?.TotalDebit,
                closing?.TotalCredit));
        }

        return months;
    }

    /// <summary>The checks of the month and the totals of its journals as they would be frozen now.</summary>
    public async Task<Result<ClosingPreview>> PreviewAsync(Guid agencyId, AccountingMonth month, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        if (!await IsAgencyAsync(agencyId, cancellationToken))
        {
            return AccountingErrors.AgencyNotFound;
        }

        var period = await _db.Set<AccountingPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.AgencyId == agencyId && p.Year == month.Year && p.Month == month.Month, cancellationToken);
        var checks = await ChecksAsync(agencyId, month, cancellationToken);
        var generation = await _journals.GenerateAsync(agencyId, month, cancellationToken);
        return new ClosingPreview(
            agencyId,
            month,
            period?.Status ?? AccountingPeriodStatus.Open,
            month.IsOverOn(Today),
            checks,
            Totals(generation.Entries),
            generation.MissingKeys,
            generation.Errors);
    }

    /// <summary>
    /// Closes the month: refused when it is not over, already closed, when a blocking point remains, when a confirmable point
    /// is not among <paramref name="confirmedKeys"/>, or when its journals are incomplete or unbalanced. Returns the closing.
    /// </summary>
    public async Task<Result<Guid>> CloseAsync(Guid agencyId, AccountingMonth month, IReadOnlyCollection<string> confirmedKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedKeys);
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        if (!await IsAgencyAsync(agencyId, cancellationToken))
        {
            return AccountingErrors.AgencyNotFound;
        }

        var confirmed = confirmedKeys.ToHashSet(StringComparer.Ordinal);
        return await GuardAsync(_unitOfWork.ExecuteAsync<Guid>(
            async ct =>
            {
                var locked = await _locks.AcquireExclusiveAsync(AccountingLocks.Period(agencyId, month), ct);
                if (locked.IsFailure)
                {
                    return locked.Error;
                }

                var period = await _db.Set<AccountingPeriod>().FirstOrDefaultAsync(p => p.AgencyId == agencyId && p.Year == month.Year && p.Month == month.Month, ct);
                if (period is { IsClosed: true })
                {
                    return AccountingErrors.PeriodAlreadyClosed;
                }

                if (!month.IsOverOn(Today))
                {
                    return AccountingErrors.PeriodNotEnded;
                }

                var checks = await ChecksAsync(agencyId, month, ct);
                if (checks.Any(c => c.Severity == ClosingCheckSeverity.Blocking))
                {
                    return AccountingErrors.ClosingBlocked;
                }

                if (checks.Any(c => c.Severity == ClosingCheckSeverity.Confirmable && !confirmed.Contains(c.Key)))
                {
                    return AccountingErrors.ClosingNotConfirmed;
                }

                var generation = await _journals.GenerateAsync(agencyId, month, ct);
                if (generation.MissingKeys.Count > 0)
                {
                    return AccountingErrors.MappingMissing;
                }

                if (generation.Errors.Count > 0)
                {
                    return AccountingErrors.JournalsUnbalanced;
                }

                var actor = _user.ToAuditActor();
                var now = _clock.GetUtcNow();
                var confirmedChecks = string.Join(
                    "; ",
                    checks.Where(c => c.Severity == ClosingCheckSeverity.Confirmable).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Kind} {c.Subject}")));
                var closing = PeriodClosing.Freeze(agencyId, month, generation.Entries, confirmedChecks, actor, now);
                if (closing.IsFailure)
                {
                    return closing.Error;
                }

                if (period is null)
                {
                    var started = AccountingPeriod.Start(agencyId, month);
                    if (started.IsFailure)
                    {
                        return started.Error;
                    }

                    period = started.Value;
                    _db.Set<AccountingPeriod>().Add(period);
                }

                var closed = period.Close(closing.Value.Id, Today, actor, now);
                if (closed.IsFailure)
                {
                    return closed.Error;
                }

                _db.Set<PeriodClosing>().Add(closing.Value);
                await _audit.RecordAsync(
                    new AuditEvent("accounting.period_closed")
                    {
                        EntityType = nameof(AccountingPeriod),
                        EntityId = period.Id.ToString(),
                        Details = string.Create(CultureInfo.InvariantCulture, $"{month}: {closing.Value.EntryCount} entries frozen, debits = credits = {closing.Value.TotalDebit:F}"),
                    },
                    ct);
                return closing.Value.Id;
            },
            cancellationToken: cancellationToken));
    }

    /// <summary>Reopens a closed month with a reason (manager, <c>accounting.reopen</c>): facts may be dated in it again; audited.</summary>
    public async Task<Result> ReopenAsync(Guid agencyId, AccountingMonth month, string? reason, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.AccountingReopen))
        {
            return AccountingErrors.Forbidden;
        }

        var reopened = await GuardAsync(_unitOfWork.ExecuteAsync<bool>(
            async ct =>
            {
                var locked = await _locks.AcquireExclusiveAsync(AccountingLocks.Period(agencyId, month), ct);
                if (locked.IsFailure)
                {
                    return locked.Error;
                }

                var period = await _db.Set<AccountingPeriod>().FirstOrDefaultAsync(p => p.AgencyId == agencyId && p.Year == month.Year && p.Month == month.Month, ct);
                if (period is null)
                {
                    return AccountingErrors.PeriodNotClosed;
                }

                var outcome = period.Reopen(reason, _user.ToAuditActor(), _clock.GetUtcNow());
                if (outcome.IsFailure)
                {
                    return outcome.Error;
                }

                await _audit.RecordAsync(
                    new AuditEvent("accounting.period_reopened")
                    {
                        EntityType = nameof(AccountingPeriod),
                        EntityId = period.Id.ToString(),
                        Details = $"{month} — {period.ReopenReason}",
                    },
                    ct);
                return true;
            },
            cancellationToken: cancellationToken));
        return reopened.IsSuccess ? Result.Success() : reopened.Error;
    }

    private static List<JournalTotalView> Totals(IReadOnlyCollection<JournalEntry> entries) =>
    [
        .. JournalCatalog.All.Select(journal =>
        {
            var of = entries.Where(e => e.Journal == journal).ToList();
            return new JournalTotalView(journal, JournalCatalog.CodeOf(journal), of.Count, of.Select(e => e.TotalDebit).Sum(), of.Select(e => e.TotalCredit).Sum());
        }),
    ];

    private static async Task<Result<T>> GuardAsync<T>(Task<Result<T>> operation)
    {
        try
        {
            return await operation;
        }
        catch (DbUpdateException)
        {
            // Two closings of the same month at once (unique period row, row version): the first won.
            return AccountingErrors.ConcurrentUpdate;
        }
    }

    private async Task<bool> IsAgencyAsync(Guid agencyId, CancellationToken cancellationToken) =>
        (await _referential.GetAgenciesAsync(cancellationToken)).Any(a => a.Id == agencyId);

    /// <summary>The points to settle (blocking) or to confirm before closing the month.</summary>
    private async Task<IReadOnlyList<ClosingCheckView>> ChecksAsync(Guid agencyId, AccountingMonth month, CancellationToken cancellationToken)
    {
        var checks = new List<ClosingCheckView>();
        foreach (var session in await _cash.GetSessionTotalsAsync(month.FirstDay, month.LastDay, agencyId, cancellationToken))
        {
            if (session.Status != CashSessionStatusInfo.Locked)
            {
                checks.Add(Check($"cash-session:{session.SessionId:N}", ClosingCheckKind.UnlockedCashSession, $"{session.BusinessDate:dd/MM/yyyy} {session.CashierName}", session.BusinessDate, null));
            }
        }

        foreach (var difference in (await _reconciliation.DifferencesAsync(agencyId, month, cancellationToken)).Where(d => !d.IsJustified))
        {
            checks.Add(Check($"difference:{difference.Key}", ClosingCheckKind.UnjustifiedDifference, difference.Subject, difference.Date, difference.Amount));
        }

        foreach (var file in await _billingAccounting.GetFilesToInvoiceAsync(agencyId, month.FirstDay, month.LastDay, cancellationToken))
        {
            checks.Add(Check($"to-invoice:{file.BookingId:N}:{file.Kind}", ClosingCheckKind.FileToInvoice, file.BookingNumber, TunisTime.DateOf(file.StatusChangedAtUtc), file.Amount));
        }

        foreach (var document in await _billing.GetSalesDocumentsAsync(agencyId, month.FirstDay, month.LastDay, cancellationToken))
        {
            if (document.EInvoice.Status is EInvoiceStatusInfo.Pending or EInvoiceStatusInfo.Failed)
            {
                checks.Add(Check($"einvoice:{document.Id:N}", ClosingCheckKind.EInvoiceNotTransmitted, document.Number, document.IssueDate, document.TotalDue));
            }
        }

        return checks;
    }

    private static ClosingCheckView Check(string key, ClosingCheckKind kind, string subject, DateOnly? date, Money? amount) =>
        new(key, kind, JournalCatalog.SeverityOf(kind), subject, date, amount);
}
