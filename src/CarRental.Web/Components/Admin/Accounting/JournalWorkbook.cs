using System.Globalization;
using CarRental.Application.Accounting.Journals;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using ClosedXML.Excel;

namespace CarRental.Web.Components.Admin.Accounting;

/// <summary>
/// The generic Excel export of the journals (ClosedXML, ADR-0065): a sheet <c>Écritures</c> in the generic format —
/// date, journal, compte, pièce, libellé, débit, crédit, lettrage —, typed cells (dates, amounts to the millime), a bold
/// frozen header with a filter and the control total below; a sheet <c>Contrôle</c> with the agency, the months, the totals
/// per journal and whether they balance. Texts are neutralized against formula injection (<see cref="SpreadsheetText"/>).
/// TODO(spec): the format of the software of the chartered accountant.
/// </summary>
internal sealed class JournalWorkbook : IJournalExporter
{
    public const string FormatName = "xlsx";

    private const string AmountFormat = "#,##0.000";
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#E8EEF7");

    public string Format => FormatName;

    public string ContentType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public string FileExtension => "xlsx";

    public byte[] Export(JournalExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var workbook = new XLWorkbook();
        var lines = workbook.Worksheets.Add("Écritures");
        string[] headers = ["Date", "Journal", "Compte", "Pièce", "Libellé", "Débit", "Crédit", "Lettrage"];
        for (var column = 0; column < headers.Length; column++)
        {
            lines.Cell(1, column + 1).Value = headers[column];
        }

        var row = 1;
        foreach (var line in document.Lines)
        {
            row++;
            lines.Cell(row, 1).Value = line.Date.ToDateTime(TimeOnly.MinValue);
            lines.Cell(row, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            lines.Cell(row, 2).Value = SpreadsheetText.Neutralize(line.Journal);
            lines.Cell(row, 3).Value = SpreadsheetText.Neutralize(line.Account);
            lines.Cell(row, 4).Value = SpreadsheetText.Neutralize(line.Piece);
            lines.Cell(row, 5).Value = SpreadsheetText.Neutralize(line.Label);
            lines.Cell(row, 6).Value = line.Debit.Amount;
            lines.Cell(row, 7).Value = line.Credit.Amount;
            lines.Cell(row, 8).Value = SpreadsheetText.Neutralize(line.Lettering);
        }

        var header = lines.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = HeaderFill;
        if (row > 1)
        {
            lines.Range(1, 1, row, headers.Length).SetAutoFilter();
        }

        // The control total, below the filtered range.
        var total = row + 2;
        lines.Cell(total, 5).Value = "Total de contrôle";
        lines.Cell(total, 6).Value = document.TotalDebit.Amount;
        lines.Cell(total, 7).Value = document.TotalCredit.Amount;
        lines.Range(total, 1, total, headers.Length).Style.Font.Bold = true;
        lines.Columns(6, 7).Style.NumberFormat.Format = AmountFormat;
        lines.SheetView.FreezeRows(1);
        lines.Columns().AdjustToContents(1, Math.Min(row, 500));

        Control(workbook.Worksheets.Add("Contrôle"), document);
        lines.SetTabActive();
        workbook.Properties.Title = $"Journaux {document.AgencyName} {document.From} — {document.To}";
        workbook.Properties.Author = "CarRental";
        workbook.Properties.Created = TunisTime.ToTunis(document.GeneratedAtUtc).DateTime;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void Control(IXLWorksheet sheet, JournalExportDocument document)
    {
        var rows = new List<(string Label, object? Value)>
        {
            ("Agence", SpreadsheetText.Neutralize(document.AgencyName)),
            ("Du mois", document.From.ToString()),
            ("Au mois", document.To.ToString()),
            ("Journal", document.Journal is { } journal ? JournalCatalog.CodeOf(journal) : "Tous"),
            ("Écritures", document.EntryCount),
            ("Lignes", document.Lines.Count),
            ("Total des débits", document.TotalDebit.Amount),
            ("Total des crédits", document.TotalCredit.Amount),
            ("Équilibré", document.IsBalanced ? "Oui" : "Non"),
            ("Mois clos (lignes figées)", document.IsFrozen ? "Oui" : "Non"),
            ("Généré le (heure de Tunis)", TunisTime.ToTunis(document.GeneratedAtUtc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
            ("Format", "Format générique (date, journal, compte, pièce, libellé, débit, crédit, lettrage) — format de l'expert-comptable à définir"),
        };
        for (var i = 0; i < rows.Count; i++)
        {
            sheet.Cell(i + 1, 1).Value = rows[i].Label;
            var cell = sheet.Cell(i + 1, 2);
            switch (rows[i].Value)
            {
                case decimal amount:
                    cell.Value = amount;
                    cell.Style.NumberFormat.Format = AmountFormat;
                    break;
                case int count:
                    cell.Value = count;
                    break;
                default:
                    cell.Value = rows[i].Value?.ToString() ?? string.Empty;
                    break;
            }
        }

        var start = rows.Count + 2;
        string[] headers = ["Journal", "Lignes", "Débit", "Crédit", "Équilibré"];
        for (var column = 0; column < headers.Length; column++)
        {
            sheet.Cell(start, column + 1).Value = headers[column];
        }

        sheet.Range(start, 1, start, headers.Length).Style.Font.Bold = true;
        var row = start;
        foreach (var group in document.Lines.GroupBy(l => l.Journal, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            row++;
            var debit = group.Select(l => l.Debit).Sum();
            var credit = group.Select(l => l.Credit).Sum();
            sheet.Cell(row, 1).Value = group.Key;
            sheet.Cell(row, 2).Value = group.Count();
            sheet.Cell(row, 3).Value = debit.Amount;
            sheet.Cell(row, 4).Value = credit.Amount;
            sheet.Cell(row, 5).Value = debit == credit ? "Oui" : "Non";
        }

        sheet.Range(start + 1, 3, Math.Max(row, start + 1), 4).Style.NumberFormat.Format = AmountFormat;
        sheet.Range(1, 1, rows.Count, 1).Style.Font.Bold = true;
        sheet.Columns().AdjustToContents();
    }
}
