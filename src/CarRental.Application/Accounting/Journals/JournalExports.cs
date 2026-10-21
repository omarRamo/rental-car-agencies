using System.Globalization;
using System.Text;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;

namespace CarRental.Application.Accounting.Journals;

/// <summary>
/// Port of the exports of the journals (SPEC §8 "export CSV ou Excel au format convenu avec l'expert-comptable"): each
/// exporter writes the lines of the journals in its format. Today: the generic CSV (here) and the generic Excel workbook
/// (web host, ClosedXML), both in the generic format <c>date, journal, compte, pièce, libellé, débit, crédit, lettrage</c>
/// with a control total. TODO(spec): the format of the software of the chartered accountant is an exporter to add.
/// </summary>
public interface IJournalExporter
{
    /// <summary>Stable name of the format, e.g. <c>csv</c> or <c>xlsx</c>.</summary>
    string Format { get; }

    string ContentType { get; }

    /// <summary>Extension of the file, without dot.</summary>
    string FileExtension { get; }

    byte[] Export(JournalExportDocument document);
}

/// <summary>A line of the generic export: one line of a journal entry.</summary>
public sealed record JournalExportLine(DateOnly Date, string Journal, string Account, string Piece, string Label, Money Debit, Money Credit, string? Lettering);

/// <summary>
/// What an export contains: the agency, the months, the journal (all of them when <see langword="null"/>), the lines in the
/// order of the journals, the control totals and whether every month exported is closed (frozen lines).
/// </summary>
public sealed record JournalExportDocument(
    string AgencyName,
    AccountingMonth From,
    AccountingMonth To,
    Journal? Journal,
    IReadOnlyList<JournalExportLine> Lines,
    int EntryCount,
    Money TotalDebit,
    Money TotalCredit,
    bool IsFrozen,
    DateTimeOffset GeneratedAtUtc)
{
    public bool IsBalanced => TotalDebit == TotalCredit;
}

/// <summary>
/// The generic CSV export: UTF-8 with a byte order mark, semicolons, CRLF, a header line
/// <c>date;journal;compte;piece;libelle;debit;credit;lettrage</c>, ISO dates (<c>yyyy-MM-dd</c>), amounts with a dot and
/// three decimals, a last line <c>TOTAL</c> with the control totals of the debits and credits. Texts are quoted when
/// needed and a cell that would start a formula is neutralized (<see cref="SpreadsheetText"/>).
/// </summary>
public sealed class GenericCsvJournalExporter : IJournalExporter
{
    public const string FormatName = "csv";

    public static readonly IReadOnlyList<string> Header = ["date", "journal", "compte", "piece", "libelle", "debit", "credit", "lettrage"];

    public string Format => FormatName;

    public string ContentType => "text/csv; charset=utf-8";

    public string FileExtension => "csv";

    public byte[] Export(JournalExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var csv = new StringBuilder();
        AppendLine(csv, Header);
        foreach (var line in document.Lines)
        {
            AppendLine(csv,
            [
                line.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                line.Journal,
                line.Account,
                line.Piece,
                line.Label,
                Amount(line.Debit),
                Amount(line.Credit),
                line.Lettering ?? string.Empty,
            ]);
        }

        AppendLine(csv, ["TOTAL", string.Empty, string.Empty, string.Empty, string.Empty, Amount(document.TotalDebit), Amount(document.TotalCredit), string.Empty]);
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(csv.ToString());
        return [.. preamble, .. body];
    }

    /// <summary>An amount as written in the CSV: dot, three decimals, no grouping (zero for the empty side).</summary>
    public static string Amount(Money amount) => amount.Amount.ToString("0.000", CultureInfo.InvariantCulture);

    private static void AppendLine(StringBuilder csv, IReadOnlyList<string> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                csv.Append(';');
            }

            csv.Append(Quote(SpreadsheetText.Neutralize(cells[i])));
        }

        csv.Append("\r\n");
    }

    private static string Quote(string value) =>
        value.AsSpan().IndexOfAny(";\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}

/// <summary>
/// Neutralizes the text of a cell of a CSV or Excel export against formula injection (OWASP "CSV injection"): a text
/// starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return is prefixed with an apostrophe, so that
/// no spreadsheet runs it. Amounts and dates are written as values, never through this.
/// </summary>
public static class SpreadsheetText
{
    public static string Neutralize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
    }
}
