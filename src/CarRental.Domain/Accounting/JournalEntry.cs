using CarRental.Domain.Common;

namespace CarRental.Domain.Accounting;

/// <summary>
/// A journal entry (écriture) of the pre-accounting: journal, date, piece (the number of the source document), label and
/// lines whose debits equal their credits — enforced on creation, an unbalanced entry never exists. Entries are generated
/// from the source facts of a period; those of a closed month are frozen with its <see cref="PeriodClosing"/>
/// (<see cref="ClosingId"/>), never changed afterwards.
/// </summary>
/// <remarks>
/// Only the frozen copies are stored: they belong to the agency of their closing (<see cref="AgencyId"/>). An entry
/// generated from the live facts is a value of the generation, without agency, that the persistence layer refuses to store.
/// </remarks>
public sealed class JournalEntry : Entity, IAgencyOwned
{
    public const int PieceMaxLength = 40;
    public const int LabelMaxLength = 200;
    public const int SourceKeyMaxLength = 120;

    private readonly List<JournalLine> _lines = [];

    private JournalEntry()
    {
    }

    private JournalEntry(Guid id, Journal journal, DateOnly date, string piece, string label, string sourceKey)
        : base(id)
    {
        Journal = journal;
        Date = date;
        Piece = piece;
        Label = label;
        SourceKey = sourceKey;
    }

    /// <summary>The agency of the closing that froze the entry; empty for an entry generated from the live facts.</summary>
    public Guid AgencyId { get; private set; }

    /// <summary>The closing that froze the entry; <see langword="null"/> for an entry generated from the live facts.</summary>
    public Guid? ClosingId { get; private set; }

    /// <summary>Order of the entry in its closing (journal, date, piece).</summary>
    public int Sequence { get; private set; }

    public Journal Journal { get; private set; }

    public DateOnly Date { get; private set; }

    /// <summary>The number of the source document (invoice, credit note, expense…) or a reference built from it.</summary>
    public string Piece { get; private set; } = null!;

    public string Label { get; private set; } = null!;

    /// <summary>
    /// The source fact (<c>invoice:{id}</c>, <c>payment:{id}</c>…): generating the same fact again gives the same key, so a
    /// fact changed after its month was closed is posted as a correction of what was frozen, never twice.
    /// </summary>
    public string SourceKey { get; private set; } = null!;

    public IReadOnlyList<JournalLine> Lines => _lines;

    public Money TotalDebit => _lines.Select(l => l.Debit).Sum();

    public Money TotalCredit => _lines.Select(l => l.Credit).Sum();

    /// <summary>
    /// Creates a balanced entry: at least one debit and one credit, every line on one side only with a positive amount in
    /// dinars, the debits equal to the credits.
    /// </summary>
    public static Result<JournalEntry> Create(Journal journal, DateOnly date, string? piece, string? label, string? sourceKey, IEnumerable<JournalLineDraft> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var cleanPiece = Clip(piece, PieceMaxLength);
        var cleanLabel = Clip(label, LabelMaxLength);
        var cleanSource = sourceKey?.Trim();
        if (!Enum.IsDefined(journal) || cleanPiece is null || cleanLabel is null || string.IsNullOrEmpty(cleanSource) || cleanSource.Length > SourceKeyMaxLength)
        {
            return AccountingErrors.EntryInvalid;
        }

        var drafts = lines.ToList();
        if (drafts.Any(d => !d.IsValid))
        {
            return AccountingErrors.EntryInvalid;
        }

        if (!drafts.Any(d => d.Debit.IsPositive) || !drafts.Any(d => d.Credit.IsPositive))
        {
            return AccountingErrors.EntryEmpty;
        }

        if (drafts.Select(d => d.Debit).Sum() != drafts.Select(d => d.Credit).Sum())
        {
            return AccountingErrors.EntryUnbalanced;
        }

        var entry = new JournalEntry(NewId(), journal, date, cleanPiece, cleanLabel, cleanSource);
        var position = 0;
        foreach (var draft in drafts)
        {
            entry._lines.Add(JournalLine.Create(entry.Id, entry.AgencyId, ++position, draft));
        }

        return entry;
    }

    /// <summary>
    /// A copy of the entry frozen by the closing <paramref name="closingId"/> of <paramref name="agencyId"/> at
    /// <paramref name="sequence"/>.
    /// </summary>
    internal JournalEntry FreezeCopy(Guid closingId, Guid agencyId, int sequence)
    {
        var copy = new JournalEntry(NewId(), Journal, Date, Piece, Label, SourceKey) { AgencyId = agencyId, ClosingId = closingId, Sequence = sequence };
        foreach (var line in _lines)
        {
            copy._lines.Add(JournalLine.Create(copy.Id, agencyId, line.Position, new JournalLineDraft(line.AccountNumber, line.Label, line.Debit, line.Credit, line.LetteringKey)));
        }

        return copy;
    }

    private static string? Clip(string? value, int maxLength)
    {
        var cleaned = value?.Trim();
        if (string.IsNullOrEmpty(cleaned))
        {
            return null;
        }

        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }
}

/// <summary>A line of a <see cref="JournalEntry"/>: account, label, debit or credit, lettering key.</summary>
public sealed class JournalLine : Entity, IAgencyOwned
{
    public const int LetteringKeyMaxLength = 40;

    private JournalLine()
    {
    }

    private JournalLine(Guid id, Guid entryId, Guid agencyId, int position, JournalLineDraft draft)
        : base(id)
    {
        EntryId = entryId;
        AgencyId = agencyId;
        Position = position;
        AccountNumber = draft.AccountNumber;
        Label = draft.Label;
        Debit = draft.Debit;
        Credit = draft.Credit;
        LetteringKey = draft.LetteringKey;
    }

    public Guid EntryId { get; private set; }

    /// <summary>The agency of the entry.</summary>
    public Guid AgencyId { get; private set; }

    public int Position { get; private set; }

    public string AccountNumber { get; private set; } = null!;

    public string Label { get; private set; } = null!;

    public Money Debit { get; private set; }

    public Money Credit { get; private set; }

    /// <summary>
    /// Lettering key (lettrage): the lines of an invoice and of the payments that settle it share the key of their rental
    /// file, those of an expense and of its payment the number of the expense.
    /// </summary>
    public string? LetteringKey { get; private set; }

    /// <summary>Debit minus credit.</summary>
    public Money Balance => Debit - Credit;

    internal static JournalLine Create(Guid entryId, Guid agencyId, int position, JournalLineDraft draft) => new(NewId(), entryId, agencyId, position, draft);
}

/// <summary>A line before it joins an entry: exactly one side positive, in dinars.</summary>
public sealed record JournalLineDraft(string AccountNumber, string Label, Money Debit, Money Credit, string? LetteringKey = null)
{
    public bool IsValid =>
        LedgerAccount.ValidateNumber(AccountNumber).IsSuccess
        && !string.IsNullOrWhiteSpace(Label)
        && Label.Length <= JournalEntry.LabelMaxLength
        && (LetteringKey is null || (LetteringKey.Length is > 0 and <= JournalLine.LetteringKeyMaxLength))
        && Debit.Currency == Currency.Tnd
        && Credit.Currency == Currency.Tnd
        && !Debit.IsNegative
        && !Credit.IsNegative
        && (Debit.IsPositive ^ Credit.IsPositive);
}

/// <summary>
/// Builds a balanced <see cref="JournalEntry"/> from signed amounts: a positive amount is a debit, a negative one a credit
/// (a credit note posts the same accounts as an invoice with the opposite signs). Amounts of the same account, label and
/// lettering key are added up; zero lines are dropped.
/// </summary>
public sealed class JournalEntryBuilder(Journal journal, DateOnly date, string piece, string label, string sourceKey)
{
    private readonly List<(string Account, string Label, string? Lettering, Money Amount)> _amounts = [];

    public Journal Journal => journal;

    public DateOnly Date => date;

    public string SourceKey => sourceKey;

    /// <summary>Adds <paramref name="amount"/> on the debit side of <paramref name="account"/> (a negative amount goes to the credit side).</summary>
    public JournalEntryBuilder Debit(string account, Money amount, string? lineLabel = null, string? lettering = null) =>
        Add(account, amount, lineLabel, lettering);

    /// <summary>Adds <paramref name="amount"/> on the credit side of <paramref name="account"/> (a negative amount goes to the debit side).</summary>
    public JournalEntryBuilder Credit(string account, Money amount, string? lineLabel = null, string? lettering = null) =>
        Add(account, amount.Negate(), lineLabel, lettering);

    /// <summary>
    /// The entry; <see langword="null"/> when every amount nets to zero (nothing to post). Fails when the signed amounts do
    /// not add up to zero.
    /// </summary>
    public Result<JournalEntry?> Build()
    {
        var lines = _amounts
            .GroupBy(a => (a.Account, a.Label, a.Lettering))
            .Select(g => (g.Key.Account, g.Key.Label, g.Key.Lettering, Amount: g.Select(a => a.Amount).Sum()))
            .Where(l => !l.Amount.IsZero)
            .Select(l => new JournalLineDraft(
                l.Account,
                l.Label,
                l.Amount.IsPositive ? l.Amount : Money.Zero,
                l.Amount.IsNegative ? l.Amount.Negate() : Money.Zero,
                l.Lettering))
            .OrderBy(l => l.Debit.IsPositive ? 0 : 1)
            .ToList();
        if (lines.Count == 0)
        {
            return Result.Success<JournalEntry?>(null);
        }

        var entry = JournalEntry.Create(journal, date, piece, label, sourceKey, lines);
        return entry.IsSuccess ? entry.Value : Result.Failure<JournalEntry?>(entry.Error);
    }

    private JournalEntryBuilder Add(string account, Money amount, string? lineLabel, string? lettering)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        var text = (lineLabel ?? label).Trim();
        _amounts.Add((
            account,
            text.Length > JournalEntry.LabelMaxLength ? text[..JournalEntry.LabelMaxLength] : text,
            string.IsNullOrWhiteSpace(lettering) ? null : lettering.Trim(),
            amount));
        return this;
    }
}
