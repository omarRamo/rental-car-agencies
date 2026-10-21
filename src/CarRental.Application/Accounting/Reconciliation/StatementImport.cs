using System.Globalization;
using System.Text;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;

namespace CarRental.Application.Accounting.Reconciliation;

/// <summary>
/// Port of the import of the merchant statements of Clic to Pay (ADR-0039 "import du relevé marchand", ADR-0064):
/// each importer reads one file format into statement lines. TODO(spec): the format of the statement of SMT is unknown;
/// the generic CSV (<see cref="GenericCsvStatementImporter"/>) and the manual entry stand in for it.
/// </summary>
public interface IMerchantStatementImporter
{
    /// <summary>Stable name of the format, e.g. <c>generic-csv</c>.</summary>
    string Format { get; }

    /// <summary>The lines of the file, or the numbers of the lines that could not be read (1 = the header).</summary>
    StatementReadResult Read(byte[] content);
}

/// <summary>What an importer read: the lines, the numbers of the invalid lines of the file, whether the header was understood.</summary>
public sealed record StatementReadResult(IReadOnlyList<StatementLineInput> Lines, IReadOnlyList<int> InvalidLines, bool HeaderInvalid)
{
    public bool IsValid => !HeaderInvalid && InvalidLines.Count == 0 && Lines.Count > 0;
}

/// <summary>
/// The generic CSV statement (documented in ADR-0064; TODO(spec) until the format of SMT is known): UTF-8 (byte order mark
/// accepted), a header line, columns separated by semicolons or commas (the header decides), fields quoted with double
/// quotes when needed. Columns, by name (case and accents ignored, any order):
/// <list type="bullet">
/// <item><c>date</c> (aliases <c>transaction_date</c>, <c>date_transaction</c>): <c>yyyy-MM-dd</c> or <c>dd/MM/yyyy</c>, required;</item>
/// <item><c>reference</c> (<c>order</c>, <c>order_number</c>, <c>orderid</c>, <c>commande</c>): our order number (32 hexadecimal
/// characters) or the order id of Clic to Pay, required;</item>
/// <item><c>type</c> (<c>kind</c>, <c>sens</c>): <c>payment</c> / <c>paiement</c> / <c>P</c>, or <c>refund</c> / <c>remboursement</c> / <c>R</c>, required;</item>
/// <item><c>amount</c> (<c>montant</c>): positive, in dinars, a dot or a comma, three decimals at most, required;</item>
/// <item><c>fee</c> (<c>commission</c>, <c>frais</c>): the commission of the bank on the line, optional;</item>
/// <item><c>settlement_date</c> (<c>settled_on</c>, <c>date_remise</c>, <c>remise</c>): the day the bank paid the line, optional;</item>
/// <item><c>note</c> (<c>libelle</c>, <c>label</c>): optional.</item>
/// </list>
/// </summary>
public sealed class GenericCsvStatementImporter : IMerchantStatementImporter
{
    public const string FormatName = "generic-csv";

    private static readonly string[] DateNames = ["date", "transaction_date", "date_transaction"];
    private static readonly string[] ReferenceNames = ["reference", "order", "order_number", "orderid", "order_id", "commande"];
    private static readonly string[] KindNames = ["type", "kind", "sens"];
    private static readonly string[] AmountNames = ["amount", "montant"];
    private static readonly string[] FeeNames = ["fee", "commission", "frais"];
    private static readonly string[] SettlementNames = ["settlement_date", "settled_on", "date_remise", "remise"];
    private static readonly string[] NoteNames = ["note", "libelle", "label"];

    public string Format => FormatName;

    public StatementReadResult Read(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = Encoding.UTF8.GetString(content).TrimStart('﻿');
        var rows = text.Split('\n').Select(r => r.TrimEnd('\r')).ToList();
        var headerIndex = rows.FindIndex(r => !string.IsNullOrWhiteSpace(r));
        if (headerIndex < 0)
        {
            return new StatementReadResult([], [], HeaderInvalid: true);
        }

        var header = rows[headerIndex];
        var separator = header.Count(c => c == ';') >= header.Count(c => c == ',') ? ';' : ',';
        var names = Split(header, separator).Select(Normalize).ToList();
        int Column(string[] aliases) => names.FindIndex(n => aliases.Contains(n, StringComparer.Ordinal));
        var (date, reference, kind, amount) = (Column(DateNames), Column(ReferenceNames), Column(KindNames), Column(AmountNames));
        if (date < 0 || reference < 0 || kind < 0 || amount < 0)
        {
            return new StatementReadResult([], [headerIndex + 1], HeaderInvalid: true);
        }

        var (fee, settlement, note) = (Column(FeeNames), Column(SettlementNames), Column(NoteNames));
        var lines = new List<StatementLineInput>();
        var invalid = new List<int>();
        for (var i = headerIndex + 1; i < rows.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(rows[i]))
            {
                continue;
            }

            var cells = Split(rows[i], separator);
            string? Cell(int column) => column >= 0 && column < cells.Count && !string.IsNullOrWhiteSpace(cells[column]) ? cells[column].Trim() : null;
            var line = ParseLine(Cell(date), Cell(reference), Cell(kind), Cell(amount), Cell(fee), Cell(settlement), Cell(note));
            if (line is { IsValid: true })
            {
                lines.Add(line);
            }
            else
            {
                invalid.Add(i + 1);
            }
        }

        return new StatementReadResult(lines, invalid, HeaderInvalid: false);
    }

    /// <summary>A line from its texts, or <see langword="null"/> when a required value is missing or unreadable.</summary>
    public static StatementLineInput? ParseLine(string? date, string? reference, string? kind, string? amount, string? fee, string? settlement, string? note)
    {
        if (ParseDate(date) is not { } transactionDate || reference is null || ParseKind(kind) is not { } lineKind || ParseAmount(amount) is not { } value)
        {
            return null;
        }

        Money? feeValue = null;
        if (fee is not null)
        {
            if (ParseAmount(fee) is not { } parsedFee)
            {
                return null;
            }

            feeValue = parsedFee;
        }

        DateOnly? settledOn = null;
        if (settlement is not null)
        {
            if (ParseDate(settlement) is not { } parsedSettlement)
            {
                return null;
            }

            settledOn = parsedSettlement;
        }

        return new StatementLineInput(transactionDate, reference, lineKind, value, feeValue, settledOn, note);
    }

    private static DateOnly? ParseDate(string? value) =>
        value is not null && DateOnly.TryParseExact(value, ["yyyy-MM-dd", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    private static StatementLineKind? ParseKind(string? value) => Normalize(value ?? string.Empty) switch
    {
        "payment" or "paiement" or "p" or "debit" => StatementLineKind.Payment,
        "refund" or "remboursement" or "r" or "credit" => StatementLineKind.Refund,
        _ => null,
    };

    private static Money? ParseAmount(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var cleaned = value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        if (!decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        var money = Money.FromExact(amount, Currency.Tnd);
        return money.IsSuccess ? money.Value : null;
    }

    /// <summary>Lower case, without accents nor spaces around (header names and kinds).</summary>
    private static string Normalize(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        {
            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString().Replace(' ', '_');
    }

    /// <summary>Splits a CSV row on the separator, honouring double quotes (a doubled quote is a quote).</summary>
    private static List<string> Split(string row, char separator)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < row.Length; i++)
        {
            var c = row[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < row.Length && row[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }
}
