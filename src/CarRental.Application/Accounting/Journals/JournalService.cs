using System.Globalization;
using CarRental.Application.Accounting.Chart;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Accounting.Journals;

/// <summary>A line of an entry with the label of its account in the chart.</summary>
public sealed record JournalLineView(string AccountNumber, string? AccountLabel, string Label, Money Debit, Money Credit, string? LetteringKey);

/// <summary>An entry of a journal.</summary>
public sealed record JournalEntryView(
    Guid Id,
    Journal Journal,
    string JournalCode,
    DateOnly Date,
    string Piece,
    string Label,
    string SourceKey,
    IReadOnlyList<JournalLineView> Lines,
    Money TotalDebit,
    Money TotalCredit);

/// <summary>The totals of a journal over the period.</summary>
public sealed record JournalTotalView(Journal Journal, string Code, int EntryCount, Money Debit, Money Credit)
{
    public bool IsBalanced => Debit == Credit;
}

/// <summary>The movements of an account over the period: debits, credits and the balance (debit minus credit).</summary>
public sealed record AccountBalanceView(string AccountNumber, string? AccountLabel, Money Debit, Money Credit)
{
    public Money Balance => Debit - Credit;
}

/// <summary>
/// The journals of a month of an agency: frozen when the month is closed, generated from the live facts otherwise; the
/// totals per journal and per account; what keeps the generation from being complete (operation types without account,
/// entries that cannot balance).
/// </summary>
public sealed record JournalMonthView(
    Guid AgencyId,
    string AgencyName,
    AccountingMonth Month,
    bool IsClosed,
    DateTimeOffset? ClosedAtUtc,
    string? ClosedByName,
    IReadOnlyList<JournalEntryView> Entries,
    IReadOnlyList<JournalTotalView> Totals,
    IReadOnlyList<AccountBalanceView> Balances,
    IReadOnlyList<string> MissingKeys,
    IReadOnlyList<string> Errors)
{
    public Money TotalDebit => Totals.Select(t => t.Debit).Sum();

    public Money TotalCredit => Totals.Select(t => t.Credit).Sum();

    public bool IsBalanced => TotalDebit == TotalCredit && Totals.All(t => t.IsBalanced);

    public bool IsComplete => MissingKeys.Count == 0 && Errors.Count == 0;
}

/// <summary>An exported file.</summary>
public sealed record JournalFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// The journals of the pre-accounting (<c>/admin/accounting</c>, permission <c>accounting.manage</c>, ADR-0065): generated
/// per month from the source facts — invoices and credit notes, expenses, payments, deposits, cash movements and counted
/// differences, Booking.com payouts, Clic to Pay settlements — or, for a closed month, as frozen by its closing (the same
/// lines whenever it is exported). A fact changed after the closing of its month (a payout corrected later) is posted in
/// the open month as the correction of what was frozen, never twice. Exports through the <see cref="IJournalExporter"/>s.
/// </summary>
public sealed class JournalService
{
    /// <summary>At most this many months in one export.</summary>
    public const int MaxExportMonths = 12;

    private readonly IAppDbContext _db;
    private readonly IUserContext _user;
    private readonly IReferentialQueries _referential;
    private readonly IBillingQueries _billing;
    private readonly ICashAccountingQueries _cash;
    private readonly ICashQueries _cashSessions;
    private readonly IPaymentsAccountingQueries _payments;
    private readonly IChannelPayouts _payouts;
    private readonly IBookingNumbers _bookings;
    private readonly IJournalTexts _texts;
    private readonly IEnumerable<IJournalExporter> _exporters;
    private readonly TimeProvider _clock;

    internal JournalService(
        IAppDbContext db,
        IUserContext user,
        IReferentialQueries referential,
        IBillingQueries billing,
        ICashAccountingQueries cash,
        ICashQueries cashSessions,
        IPaymentsAccountingQueries payments,
        IChannelPayouts payouts,
        IBookingNumbers bookings,
        IJournalTexts texts,
        IEnumerable<IJournalExporter> exporters,
        TimeProvider clock)
    {
        _db = db;
        _user = user;
        _referential = referential;
        _billing = billing;
        _cash = cash;
        _cashSessions = cashSessions;
        _payments = payments;
        _payouts = payouts;
        _bookings = bookings;
        _texts = texts;
        _exporters = exporters;
        _clock = clock;
    }

    /// <summary>The formats of the exports.</summary>
    public IReadOnlyList<string> Formats => [.. _exporters.Select(e => e.Format)];

    /// <summary>The journals of a month (one journal when <paramref name="journal"/> is given; the totals cover every journal).</summary>
    public async Task<Result<JournalMonthView>> GetMonthAsync(Guid agencyId, AccountingMonth month, Journal? journal = null, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.AccountingManage))
        {
            return AccountingErrors.Forbidden;
        }

        var agency = await AgencyAsync(agencyId, cancellationToken);
        if (agency is null)
        {
            return AccountingErrors.AgencyNotFound;
        }

        var journals = await JournalsOfAsync(agencyId, month, cancellationToken);
        var labels = await AccountLabelsAsync(cancellationToken);
        var shown = journal is { } wanted ? journals.Entries.Where(e => e.Journal == wanted) : journals.Entries;
        return new JournalMonthView(
            agencyId,
            agency.Name,
            month,
            journals.Period?.IsClosed == true,
            journals.Period?.IsClosed == true ? journals.Period.ClosedAtUtc : null,
            journals.Period?.IsClosed == true ? journals.Period.ClosedByName : null,
            [.. shown.Select(e => ToView(e, labels))],
            TotalsOf(journals.Entries),
            BalancesOf(journals.Entries, labels),
            journals.MissingKeys,
            journals.Errors);
    }

    /// <summary>
    /// Exports the journals of the months <paramref name="from"/> to <paramref name="to"/> (12 at most), one journal or all,
    /// in <paramref name="format"/> (<c>csv</c>, <c>xlsx</c>…), with the control totals.
    /// </summary>
    public async Task<Result<JournalFile>> ExportAsync(Guid agencyId, AccountingMonth from, AccountingMonth to, Journal? journal, string format, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.AccountingManage))
        {
            return AccountingErrors.Forbidden;
        }

        var exporter = _exporters.FirstOrDefault(e => string.Equals(e.Format, format, StringComparison.OrdinalIgnoreCase));
        if (exporter is null)
        {
            return AccountingErrors.ExportFormatUnknown;
        }

        if (to < from || (to.Year * 12) + to.Month - (from.Year * 12) - from.Month >= MaxExportMonths)
        {
            return AccountingErrors.MonthInvalid;
        }

        var agency = await AgencyAsync(agencyId, cancellationToken);
        if (agency is null)
        {
            return AccountingErrors.AgencyNotFound;
        }

        var entries = new List<JournalEntry>();
        var frozen = true;
        for (var month = from; month <= to; month = month.Next())
        {
            var journals = await JournalsOfAsync(agencyId, month, cancellationToken);
            if (!journals.IsComplete)
            {
                return journals.MissingKeys.Count > 0 ? AccountingErrors.MappingMissing : AccountingErrors.JournalsUnbalanced;
            }

            frozen &= journals.Period?.IsClosed == true;
            entries.AddRange(journals.Entries.Where(e => journal is not { } wanted || e.Journal == wanted));
        }

        var ordered = JournalOrder.Sort(entries).ToList();
        var lines = ordered
            .SelectMany(e => e.Lines.OrderBy(l => l.Position).Select(l => new JournalExportLine(e.Date, JournalCatalog.CodeOf(e.Journal), l.AccountNumber, e.Piece, l.Label, l.Debit, l.Credit, l.LetteringKey)))
            .ToList();
        var document = new JournalExportDocument(
            agency.Name,
            from,
            to,
            journal,
            lines,
            ordered.Count,
            lines.Select(l => l.Debit).Sum(),
            lines.Select(l => l.Credit).Sum(),
            frozen,
            _clock.GetUtcNow());
        var period = from == to ? from.ToString() : $"{from}_{to}";
        var name = string.Create(CultureInfo.InvariantCulture, $"journaux-{(journal is { } j ? JournalCatalog.CodeOf(j) : "tous")}-{period}.{exporter.FileExtension}");
        return new JournalFile(exporter.Export(document), exporter.ContentType, name);
    }

    /// <summary>
    /// The journals of a month for the closing: generated from the live facts (with the corrections of what other closed
    /// months froze), never the frozen ones — and why they would not be complete.
    /// </summary>
    internal async Task<JournalGeneration> GenerateAsync(Guid agencyId, AccountingMonth month, CancellationToken cancellationToken)
    {
        var facts = await LoadFactsAsync(agencyId, month, cancellationToken);
        var accounts = await ChartOfAccountsService.ResolverAsync(_db, cancellationToken);
        var generated = JournalGenerator.Generate(facts, accounts, _texts);
        if (!generated.IsComplete)
        {
            return generated;
        }

        var corrected = await WithCorrectionsAsync(agencyId, month, generated.Entries, cancellationToken);
        return generated with { Entries = corrected };
    }

    private static JournalEntryView ToView(JournalEntry entry, IReadOnlyDictionary<string, string> labels) => new(
        entry.Id,
        entry.Journal,
        JournalCatalog.CodeOf(entry.Journal),
        entry.Date,
        entry.Piece,
        entry.Label,
        entry.SourceKey,
        [.. entry.Lines.OrderBy(l => l.Position).Select(l => new JournalLineView(l.AccountNumber, labels.GetValueOrDefault(l.AccountNumber), l.Label, l.Debit, l.Credit, l.LetteringKey))],
        entry.TotalDebit,
        entry.TotalCredit);

    private static List<JournalTotalView> TotalsOf(IReadOnlyCollection<JournalEntry> entries) =>
    [
        .. JournalCatalog.All.Select(journal =>
        {
            var of = entries.Where(e => e.Journal == journal).ToList();
            return new JournalTotalView(journal, JournalCatalog.CodeOf(journal), of.Count, of.Select(e => e.TotalDebit).Sum(), of.Select(e => e.TotalCredit).Sum());
        }),
    ];

    private static List<AccountBalanceView> BalancesOf(IReadOnlyCollection<JournalEntry> entries, IReadOnlyDictionary<string, string> labels) =>
    [
        .. entries
            .SelectMany(e => e.Lines)
            .GroupBy(l => l.AccountNumber, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AccountBalanceView(g.Key, labels.GetValueOrDefault(g.Key), g.Select(l => l.Debit).Sum(), g.Select(l => l.Credit).Sum())),
    ];

    private async Task<AgencyInfo?> AgencyAsync(Guid agencyId, CancellationToken cancellationToken) =>
        (await _referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Id == agencyId);

    private async Task<IReadOnlyDictionary<string, string>> AccountLabelsAsync(CancellationToken cancellationToken) =>
        await _db.Set<LedgerAccount>().AsNoTracking().ToDictionaryAsync(a => a.Number, a => a.Label, StringComparer.Ordinal, cancellationToken);

    /// <summary>The frozen journals of a closed month, else the journals generated from the live facts.</summary>
    private async Task<MonthJournals> JournalsOfAsync(Guid agencyId, AccountingMonth month, CancellationToken cancellationToken)
    {
        var period = await _db.Set<AccountingPeriod>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.AgencyId == agencyId && p.Year == month.Year && p.Month == month.Month, cancellationToken);
        if (period is { IsClosed: true, CurrentClosingId: { } closingId })
        {
            var frozen = await _db.Set<JournalEntry>()
                .AsNoTracking()
                .Include(e => e.Lines)
                .Where(e => e.ClosingId == closingId)
                .OrderBy(e => e.Sequence)
                .ToListAsync(cancellationToken);
            return new MonthJournals(period, frozen, [], []);
        }

        var generated = await GenerateAsync(agencyId, month, cancellationToken);
        return new MonthJournals(period, generated.Entries, generated.MissingKeys, generated.Errors);
    }

    private async Task<JournalFacts> LoadFactsAsync(Guid agencyId, AccountingMonth month, CancellationToken cancellationToken)
    {
        var (from, to) = (month.FirstDay, month.LastDay);
        var sales = await _billing.GetSalesDocumentsAsync(agencyId, from, to, cancellationToken);
        var expenses = await _cash.GetExpensesAsync(agencyId, from, to, cancellationToken);
        var payments = await _payments.GetSettledPaymentsAsync(agencyId, from, to, cancellationToken);
        var deposits = await _payments.GetDepositMovementsAsync(agencyId, from, to, cancellationToken);
        var movements = await _cash.GetMovementsAsync(agencyId, from, to, cancellationToken);
        var sessions = await _cashSessions.GetSessionTotalsAsync(from, to, agencyId, cancellationToken);
        var payouts = await _payouts.GetRecordedPayoutsAsync(agencyId, from, to, cancellationToken);
        var settlements = await SettlementsAsync(agencyId, from, to, cancellationToken);
        var bookingIds = payments.Select(p => p.BookingId)
            .Concat(deposits.Select(d => d.BookingId))
            .Concat(movements.Where(m => m.BookingId is not null).Select(m => m.BookingId!.Value))
            .Distinct()
            .ToList();
        var numbers = await _bookings.GetNumbersAsync(bookingIds, cancellationToken);
        return new JournalFacts(agencyId, month, sales, expenses, payments, deposits, movements, sessions, payouts, settlements, numbers);
    }

    /// <summary>The lines of the Clic to Pay statements settled on the days, added up per settlement day.</summary>
    private async Task<IReadOnlyList<ClicToPaySettlementFact>> SettlementsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var lines = await _db.Set<MerchantStatement>()
            .AsNoTracking()
            .Where(s => s.AgencyId == agencyId)
            .SelectMany(s => s.Lines)
            .Where(l => l.SettledOn != null && l.SettledOn >= from && l.SettledOn <= to)
            .ToListAsync(cancellationToken);
        return
        [
            .. lines
                .GroupBy(l => l.SettledOn!.Value)
                .OrderBy(g => g.Key)
                .Select(g => new ClicToPaySettlementFact(g.Key, g.Select(l => l.SignedAmount).Sum(), g.Select(l => l.Fee ?? Money.Zero).Sum())),
        ];
    }

    /// <summary>
    /// Replaces each generated entry whose source fact was frozen by the closing of another month by its correction: the
    /// difference between what it is now and what was frozen (nothing when equal).
    /// </summary>
    private async Task<IReadOnlyList<JournalEntry>> WithCorrectionsAsync(Guid agencyId, AccountingMonth month, IReadOnlyList<JournalEntry> generated, CancellationToken cancellationToken)
    {
        var closings = await _db.Set<AccountingPeriod>()
            .AsNoTracking()
            .Where(p => p.AgencyId == agencyId && p.Status == AccountingPeriodStatus.Closed && p.CurrentClosingId != null && !(p.Year == month.Year && p.Month == month.Month))
            .Select(p => new { ClosingId = p.CurrentClosingId!.Value, p.Year, p.Month })
            .ToListAsync(cancellationToken);
        if (closings.Count == 0 || generated.Count == 0)
        {
            return generated;
        }

        var closingIds = closings.Select(c => c.ClosingId).ToList();
        var monthOf = closings.ToDictionary(c => c.ClosingId, c => AccountingMonth.Create(c.Year, c.Month).Value);
        var frozen = new List<JournalEntry>();
        foreach (var keys in generated.Select(e => e.SourceKey).Distinct(StringComparer.Ordinal).Chunk(500))
        {
            var batch = keys.ToList();
            frozen.AddRange(await _db.Set<JournalEntry>()
                .AsNoTracking()
                .Include(e => e.Lines)
                .Where(e => e.ClosingId != null && closingIds.Contains(e.ClosingId.Value) && batch.Contains(e.SourceKey))
                .ToListAsync(cancellationToken));
        }

        if (frozen.Count == 0)
        {
            return generated;
        }

        var frozenBySource = frozen.ToLookup(e => e.SourceKey, StringComparer.Ordinal);
        var result = new List<JournalEntry>(generated.Count);
        foreach (var entry in generated)
        {
            var before = frozenBySource[entry.SourceKey].ToList();
            if (before.Count == 0)
            {
                result.Add(entry);
                continue;
            }

            var amounts = new Dictionary<(string Account, string? Lettering), Money>();
            foreach (var line in entry.Lines)
            {
                amounts[(line.AccountNumber, line.LetteringKey)] = amounts.GetValueOrDefault((line.AccountNumber, line.LetteringKey)) + line.Balance;
            }

            foreach (var line in before.SelectMany(e => e.Lines))
            {
                amounts[(line.AccountNumber, line.LetteringKey)] = amounts.GetValueOrDefault((line.AccountNumber, line.LetteringKey)) - line.Balance;
            }

            var frozenIn = before.Select(e => monthOf[e.ClosingId!.Value]).Max();
            var label = _texts.Text(JournalText.Correction, entry.Label, frozenIn.ToString());
            var correction = new JournalEntryBuilder(entry.Journal, entry.Date, entry.Piece, label, entry.SourceKey);
            foreach (var ((account, lettering), amount) in amounts)
            {
                correction.Debit(account, amount, label, lettering);
            }

            var built = correction.Build();
            if (built.IsSuccess && built.Value is { } corrected)
            {
                result.Add(corrected);
            }
        }

        return result;
    }

    private sealed record MonthJournals(AccountingPeriod? Period, IReadOnlyList<JournalEntry> Entries, IReadOnlyList<string> MissingKeys, IReadOnlyList<string> Errors)
    {
        public bool IsComplete => MissingKeys.Count == 0 && Errors.Count == 0;
    }
}
