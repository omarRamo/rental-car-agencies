using CarRental.Domain.Common;

namespace CarRental.Domain.Accounting;

/// <summary>
/// The justification of a difference found by a reconciliation (SPEC §8 "écarts listés et justifiés"): the difference it
/// explains (its stable key, its area, its amount when it was justified), the reason, who and when. Justifications are only
/// added, never changed nor removed: the difference stays visible with its history.
/// </summary>
public sealed class ReconciliationJustification : AggregateRoot
{
    public const int ReasonMinLength = 5;
    public const int ReasonMaxLength = 500;
    public const int KeyMaxLength = 120;

    private ReconciliationJustification()
    {
    }

    private ReconciliationJustification(Guid id, Guid agencyId, AccountingMonth month, ReconciliationArea area, string key, Money amount, string reason, AuditActor by, DateTimeOffset at)
        : base(id)
    {
        AgencyId = agencyId;
        Year = month.Year;
        Month = month.Month;
        Area = area;
        DifferenceKey = key;
        Amount = amount;
        Reason = reason;
        JustifiedAtUtc = at;
        JustifiedByUserId = by.UserId;
        JustifiedByName = by.UserName is { Length: > AuditActor.UserNameMaxLength } name ? name[..AuditActor.UserNameMaxLength] : by.UserName;
    }

    public Guid AgencyId { get; private set; }

    public int Year { get; private set; }

    public int Month { get; private set; }

    public ReconciliationArea Area { get; private set; }

    /// <summary>The difference explained, e.g. <c>cash:variance:{session}</c> or <c>clictopay:missing:{payment}</c>.</summary>
    public string DifferenceKey { get; private set; } = null!;

    /// <summary>The signed amount of the difference when it was justified (a later change of the difference shows again).</summary>
    public Money Amount { get; private set; }

    public string Reason { get; private set; } = null!;

    public DateTimeOffset JustifiedAtUtc { get; private set; }

    public Guid? JustifiedByUserId { get; private set; }

    public string? JustifiedByName { get; private set; }

    public static Result<ReconciliationJustification> Justify(
        Guid agencyId,
        AccountingMonth month,
        ReconciliationArea area,
        string differenceKey,
        Money amount,
        string? reason,
        AuditActor by,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        ArgumentException.ThrowIfNullOrWhiteSpace(differenceKey);
        if (agencyId == Guid.Empty)
        {
            return AccountingErrors.AgencyNotFound;
        }

        if (!Enum.IsDefined(area) || differenceKey.Length > KeyMaxLength)
        {
            return AccountingErrors.DifferenceNotFound;
        }

        var text = reason?.Trim();
        if (text is null || text.Length < ReasonMinLength || text.Length > ReasonMaxLength)
        {
            return AccountingErrors.JustificationReasonInvalid;
        }

        var justification = new ReconciliationJustification(NewId(), agencyId, month, area, differenceKey, amount, text, by, at);
        justification.Raise(new ReconciliationDifferenceJustified(justification.Id, agencyId, month.Year, month.Month, area));
        return justification;
    }
}

/// <summary>
/// A merchant statement of Clic to Pay brought into the platform to reconcile the online payments (SPEC §8, ADR-0039
/// "import du relevé marchand") — imported from a file through a statement importer, or typed line by line from the
/// merchant portal. TODO(spec): the format of the SMT statement is unknown; the generic CSV format is documented in
/// ADR-0064.
/// </summary>
public sealed class MerchantStatement : AggregateRoot
{
    public const int MaxLines = 5000;
    public const int FileNameMaxLength = 200;
    public const string ClicToPay = "clictopay";

    private readonly List<StatementLine> _lines = [];

    private MerchantStatement()
    {
    }

    private MerchantStatement(Guid id, Guid agencyId, StatementOrigin origin, string? fileName, string format, AuditActor by, DateTimeOffset at)
        : base(id)
    {
        AgencyId = agencyId;
        Provider = ClicToPay;
        Origin = origin;
        FileName = fileName;
        Format = format;
        ImportedAtUtc = at;
        ImportedByUserId = by.UserId;
        ImportedByName = by.UserName is { Length: > AuditActor.UserNameMaxLength } name ? name[..AuditActor.UserNameMaxLength] : by.UserName;
    }

    public Guid AgencyId { get; private set; }

    public string Provider { get; private set; } = null!;

    public StatementOrigin Origin { get; private set; }

    /// <summary>The name of the imported file, for display only.</summary>
    public string? FileName { get; private set; }

    /// <summary>The importer that read it (<c>generic-csv</c>, <c>manual</c>).</summary>
    public string Format { get; private set; } = null!;

    public DateTimeOffset ImportedAtUtc { get; private set; }

    public Guid? ImportedByUserId { get; private set; }

    public string? ImportedByName { get; private set; }

    public IReadOnlyList<StatementLine> Lines => _lines;

    public static Result<MerchantStatement> Record(
        Guid agencyId,
        StatementOrigin origin,
        string? fileName,
        string format,
        IReadOnlyCollection<StatementLineInput> lines,
        AuditActor by,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(by);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        if (agencyId == Guid.Empty)
        {
            return AccountingErrors.AgencyNotFound;
        }

        if (!Enum.IsDefined(origin))
        {
            return AccountingErrors.StatementInvalid;
        }

        if (lines.Count == 0)
        {
            return AccountingErrors.StatementEmpty;
        }

        if (lines.Count > MaxLines)
        {
            return AccountingErrors.StatementTooLarge;
        }

        if (lines.Any(l => !l.IsValid))
        {
            return AccountingErrors.StatementLineInvalid;
        }

        var name = fileName?.Trim();
        var statement = new MerchantStatement(
            NewId(),
            agencyId,
            origin,
            string.IsNullOrEmpty(name) ? null : name.Length > FileNameMaxLength ? name[..FileNameMaxLength] : name,
            format,
            by,
            at);
        var position = 0;
        foreach (var line in lines)
        {
            statement._lines.Add(StatementLine.Create(statement.Id, ++position, line));
        }

        statement.Raise(new MerchantStatementRecorded(statement.Id, agencyId, statement._lines.Count));
        return statement;
    }

    /// <summary>The Tunis days the statement touches (transactions and settlements), for the closing of the months.</summary>
    public IEnumerable<DateOnly> Days() =>
        _lines.Select(l => l.TransactionDate).Concat(_lines.Where(l => l.SettledOn is not null).Select(l => l.SettledOn!.Value)).Distinct();
}

/// <summary>A line of a merchant statement: the transaction, the order it belongs to, its amount and, when known, the fee and the settlement.</summary>
public sealed class StatementLine : Entity
{
    public const int ReferenceMaxLength = 64;
    public const int NoteMaxLength = 200;

    private StatementLine()
    {
    }

    private StatementLine(Guid id, Guid statementId, int position, StatementLineInput input)
        : base(id)
    {
        StatementId = statementId;
        Position = position;
        TransactionDate = input.TransactionDate;
        Reference = input.Reference.Trim();
        Kind = input.Kind;
        Amount = input.Amount;
        Fee = input.Fee;
        SettledOn = input.SettledOn;
        Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
    }

    public Guid StatementId { get; private set; }

    public int Position { get; private set; }

    public DateOnly TransactionDate { get; private set; }

    /// <summary>Our order number (32 hexadecimal characters) or the order id of the gateway.</summary>
    public string Reference { get; private set; } = null!;

    public StatementLineKind Kind { get; private set; }

    /// <summary>The amount of the transaction (always positive; a refund is a <see cref="StatementLineKind.Refund"/> line).</summary>
    public Money Amount { get; private set; }

    /// <summary>The commission of the bank on the line, when the statement gives it.</summary>
    public Money? Fee { get; private set; }

    /// <summary>The day the bank paid the line to the account of the agency, when the statement gives it.</summary>
    public DateOnly? SettledOn { get; private set; }

    public string? Note { get; private set; }

    /// <summary>The signed amount for the customer: positive for a payment, negative for a refund.</summary>
    public Money SignedAmount => Kind == StatementLineKind.Refund ? Amount.Negate() : Amount;

    internal static StatementLine Create(Guid statementId, int position, StatementLineInput input) => new(Entity.NewId(), statementId, position, input);
}

/// <summary>A statement line as read by an importer or typed by hand.</summary>
public sealed record StatementLineInput(DateOnly TransactionDate, string Reference, StatementLineKind Kind, Money Amount, Money? Fee = null, DateOnly? SettledOn = null, string? Note = null)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Reference)
        && Reference.Trim().Length <= StatementLine.ReferenceMaxLength
        && Enum.IsDefined(Kind)
        && Amount.Currency == Currency.Tnd
        && Amount.IsPositive
        && Amount.Amount <= 1_000_000m
        && (Fee is null || (Fee.Value.Currency == Currency.Tnd && !Fee.Value.IsNegative && Fee.Value <= Amount))
        && (SettledOn is null || SettledOn.Value >= TransactionDate)
        && (Note is null || Note.Trim().Length <= StatementLine.NoteMaxLength);
}
