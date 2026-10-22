using CarRental.Domain.Common;

namespace CarRental.Domain.Accounting;

/// <summary>
/// A month of an agency in the pre-accounting (SPEC §8 "clôture mensuelle qui verrouille la période"): open until it is
/// closed — then no invoice, payment, expense or cash movement may be dated in it and its journals are frozen in the
/// <see cref="PeriodClosing"/> named by <see cref="CurrentClosingId"/> —, reopened only by a manager with a reason (audited,
/// the frozen closing is kept). A month without a row is open.
/// </summary>
public sealed class AccountingPeriod : AggregateRoot, IAgencyOwned
{
    public const int ReasonMinLength = 10;
    public const int ReasonMaxLength = 500;

    private AccountingPeriod()
    {
    }

    private AccountingPeriod(Guid id, Guid agencyId, AccountingMonth month)
        : base(id)
    {
        AgencyId = agencyId;
        Year = month.Year;
        Month = month.Month;
        Status = AccountingPeriodStatus.Open;
    }

    public Guid AgencyId { get; private set; }

    public int Year { get; private set; }

    public int Month { get; private set; }

    public AccountingPeriodStatus Status { get; private set; }

    /// <summary>The closing whose frozen journals are the journals of the month while it is closed.</summary>
    public Guid? CurrentClosingId { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    public Guid? ClosedByUserId { get; private set; }

    public string? ClosedByName { get; private set; }

    public DateTimeOffset? ReopenedAtUtc { get; private set; }

    public Guid? ReopenedByUserId { get; private set; }

    public string? ReopenedByName { get; private set; }

    /// <summary>Why the month was last reopened.</summary>
    public string? ReopenReason { get; private set; }

    /// <summary>How many times the month was reopened (each reopening is in the audit log and the temporal history).</summary>
    public int ReopenCount { get; private set; }

    public AccountingMonth CalendarMonth => AccountingMonth.Create(Year, Month).Value;

    public bool IsClosed => Status == AccountingPeriodStatus.Closed;

    /// <summary>The row of a month that was never closed.</summary>
    public static Result<AccountingPeriod> Start(Guid agencyId, AccountingMonth month)
    {
        if (agencyId == Guid.Empty)
        {
            return AccountingErrors.AgencyNotFound;
        }

        return new AccountingPeriod(NewId(), agencyId, month);
    }

    /// <summary>
    /// Closes the month with the frozen journals of <paramref name="closingId"/>; only once the month is over on
    /// <paramref name="today"/> (Tunis): the current month is never closed, so the activity of the day is never refused.
    /// </summary>
    public Result Close(Guid closingId, DateOnly today, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (IsClosed)
        {
            return AccountingErrors.PeriodAlreadyClosed;
        }

        if (!CalendarMonth.IsOverOn(today))
        {
            return AccountingErrors.PeriodNotEnded;
        }

        if (closingId == Guid.Empty)
        {
            throw new ArgumentException("A closed month names its frozen closing.", nameof(closingId));
        }

        Status = AccountingPeriodStatus.Closed;
        CurrentClosingId = closingId;
        ClosedAtUtc = at;
        ClosedByUserId = by.UserId;
        ClosedByName = Name(by);
        Raise(new AccountingPeriodClosed(Id, AgencyId, Year, Month, closingId));
        return Result.Success();
    }

    /// <summary>Reopens a closed month with a reason (the manager's decision): its journals are generated live again.</summary>
    public Result Reopen(string? reason, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (!IsClosed)
        {
            return AccountingErrors.PeriodNotClosed;
        }

        var text = reason?.Trim();
        if (text is null || text.Length < ReasonMinLength || text.Length > ReasonMaxLength)
        {
            return AccountingErrors.ReopenReasonRequired;
        }

        var closingId = CurrentClosingId;
        Status = AccountingPeriodStatus.Open;
        CurrentClosingId = null;
        ReopenedAtUtc = at;
        ReopenedByUserId = by.UserId;
        ReopenedByName = Name(by);
        ReopenReason = text;
        ReopenCount++;
        Raise(new AccountingPeriodReopened(Id, AgencyId, Year, Month, closingId));
        return Result.Success();
    }

    private static string? Name(AuditActor by) =>
        by.UserName is { Length: > AuditActor.UserNameMaxLength } name ? name[..AuditActor.UserNameMaxLength] : by.UserName;
}

/// <summary>
/// The journals of a month as they were when it was closed (stored, so that a closed month always exports exactly the
/// same lines): its entries and their totals. Never changed; a reopened month keeps its earlier closings for the audit.
/// </summary>
public sealed class PeriodClosing : AggregateRoot, IAgencyOwned
{
    public const int ConfirmedChecksMaxLength = 2000;

    private readonly List<JournalEntry> _entries = [];

    private PeriodClosing()
    {
    }

    private PeriodClosing(Guid id, Guid agencyId, AccountingMonth month, string? confirmedChecks, AuditActor by, DateTimeOffset at)
        : base(id)
    {
        AgencyId = agencyId;
        Year = month.Year;
        Month = month.Month;
        ConfirmedChecks = confirmedChecks;
        ClosedAtUtc = at;
        ClosedByUserId = by.UserId;
        ClosedByName = by.UserName is { Length: > AuditActor.UserNameMaxLength } name ? name[..AuditActor.UserNameMaxLength] : by.UserName;
    }

    public Guid AgencyId { get; private set; }

    public int Year { get; private set; }

    public int Month { get; private set; }

    public DateTimeOffset ClosedAtUtc { get; private set; }

    public Guid? ClosedByUserId { get; private set; }

    public string? ClosedByName { get; private set; }

    /// <summary>The confirmable points the accountant confirmed when closing (files to invoice, e-invoices…), as listed then.</summary>
    public string? ConfirmedChecks { get; private set; }

    public int EntryCount { get; private set; }

    public Money TotalDebit { get; private set; }

    public Money TotalCredit { get; private set; }

    public IReadOnlyList<JournalEntry> Entries => _entries;

    /// <summary>
    /// Freezes the journals of the month: every entry dated in the month (balanced by construction), the totals equal.
    /// The entries are copied in their order (journal, date, piece).
    /// </summary>
    public static Result<PeriodClosing> Freeze(
        Guid agencyId,
        AccountingMonth month,
        IReadOnlyCollection<JournalEntry> entries,
        string? confirmedChecks,
        AuditActor by,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(by);
        if (agencyId == Guid.Empty)
        {
            return AccountingErrors.AgencyNotFound;
        }

        if (entries.Any(e => !month.Contains(e.Date)))
        {
            return AccountingErrors.EntryOutsideMonth;
        }

        var debit = entries.Select(e => e.TotalDebit).Sum();
        var credit = entries.Select(e => e.TotalCredit).Sum();
        if (debit != credit || entries.Any(e => e.TotalDebit != e.TotalCredit))
        {
            return AccountingErrors.JournalsUnbalanced;
        }

        var checks = confirmedChecks?.Trim();
        if (checks is { Length: > ConfirmedChecksMaxLength })
        {
            checks = checks[..ConfirmedChecksMaxLength];
        }

        var closing = new PeriodClosing(NewId(), agencyId, month, string.IsNullOrEmpty(checks) ? null : checks, by, at);
        var sequence = 0;
        foreach (var entry in JournalOrder.Sort(entries))
        {
            closing._entries.Add(entry.FreezeCopy(closing.Id, agencyId, ++sequence));
        }

        closing.EntryCount = closing._entries.Count;
        closing.TotalDebit = debit;
        closing.TotalCredit = credit;
        return closing;
    }
}

/// <summary>The order of the entries in the journals and the exports: journal, date, piece, then source.</summary>
public static class JournalOrder
{
    public static IEnumerable<JournalEntry> Sort(IEnumerable<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .OrderBy(e => e.Journal)
            .ThenBy(e => e.Date)
            .ThenBy(e => e.Piece, StringComparer.Ordinal)
            .ThenBy(e => e.SourceKey, StringComparer.Ordinal);
    }
}
