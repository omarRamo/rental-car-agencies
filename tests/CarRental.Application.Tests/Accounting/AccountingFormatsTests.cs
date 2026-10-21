using System.Text;
using CarRental.Application.Accounting.Journals;
using CarRental.Application.Accounting.Reconciliation;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Accounting;

/// <summary>
/// The generic formats (ADR-0064, ADR-0065, TODO(spec) until the accountant's and SMT's formats are known): the CSV of the
/// journals (neutralized against formula injection) and the CSV of the merchant statements.
/// </summary>
public sealed class AccountingFormatsTests
{
    private static readonly AccountingMonth March = AccountingMonth.Create(2027, 3).Value;

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "'=HYPERLINK(\"http://x\")")]
    [InlineData("+216 20 000 000", "'+216 20 000 000")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("\rcmd", "'\rcmd")]
    [InlineData("Station Agil", "Station Agil")]
    [InlineData("Remise 5 %", "Remise 5 %")]
    [InlineData("", "")]
    public void A_text_that_would_start_a_formula_is_neutralized(string text, string expected) =>
        SpreadsheetText.Neutralize(text).ShouldBe(expected);

    [Fact]
    public void The_journal_csv_quotes_its_texts_and_neutralizes_the_formulas_of_the_labels()
    {
        var document = Document(
            new JournalExportLine(new DateOnly(2027, 3, 15), "AC", "6061", "DEP-2027-000001", "=CMD|'/C calc'!A0", Money.Tnd(100m), Money.Zero, "DEP-2027-000001"),
            new JournalExportLine(new DateOnly(2027, 3, 15), "AC", "401", "DEP-2027-000001", "Station \"Agil\"; Tunis", Money.Zero, Money.Tnd(100m), null));

        var bytes = new GenericCsvJournalExporter().Export(document);

        var rows = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        rows[1].ShouldBe("2027-03-15;AC;6061;DEP-2027-000001;'=CMD|'/C calc'!A0;100.000;0.000;DEP-2027-000001");
        rows[2].ShouldBe("2027-03-15;AC;401;DEP-2027-000001;\"Station \"\"Agil\"\"; Tunis\";0.000;100.000;");
        rows[3].ShouldBe("TOTAL;;;;;100.000;100.000;");
        GenericCsvJournalExporter.Amount(Money.Tnd(1234.5m)).ShouldBe("1234.500");
    }

    [Fact]
    public void The_statement_csv_reads_its_columns_by_name_in_any_order_with_accents_aliases_and_either_separator()
    {
        var semicolons = "﻿Montant;Référence;Date;Sens;Frais;Date remise;Libellé\n12,500;ABC123;15/03/2027;P;0,250;17/03/2027;\"Paiement; en ligne\"\n";
        var commas = "transaction_date,order_number,kind,amount\n2027-03-16,DEF456,refund,\"3.000\"\n";

        var first = new GenericCsvStatementImporter().Read(Encoding.UTF8.GetBytes(semicolons));
        var second = new GenericCsvStatementImporter().Read(Encoding.UTF8.GetBytes(commas));

        first.IsValid.ShouldBeTrue();
        first.Lines.ShouldHaveSingleItem().ShouldBe(new StatementLineInput(new DateOnly(2027, 3, 15), "ABC123", StatementLineKind.Payment, Money.Tnd(12.5m), Money.Tnd(0.25m), new DateOnly(2027, 3, 17), "Paiement; en ligne"));
        second.IsValid.ShouldBeTrue();
        second.Lines.ShouldHaveSingleItem().ShouldBe(new StatementLineInput(new DateOnly(2027, 3, 16), "DEF456", StatementLineKind.Refund, Money.Tnd(3m)));
    }

    [Fact]
    public void The_statement_csv_lists_the_lines_it_cannot_read_and_refuses_a_header_without_the_required_columns()
    {
        var importer = new GenericCsvStatementImporter();

        var invalid = importer.Read(Encoding.UTF8.GetBytes("date;reference;type;amount\n\n2027-13-01;A;payment;1\n2027-03-01;A;transfer;1\n2027-03-01;A;payment;-1\n2027-03-01;A;payment;1;\n"));
        var header = importer.Read(Encoding.UTF8.GetBytes("\n\ndate;reference;amount\n2027-03-01;A;1\n"));
        var empty = importer.Read([]);

        invalid.IsValid.ShouldBeFalse();
        invalid.InvalidLines.ShouldBe([3, 4, 5]);
        invalid.Lines.Count.ShouldBe(1);
        header.HeaderInvalid.ShouldBeTrue();
        header.InvalidLines.ShouldBe([3]);
        empty.HeaderInvalid.ShouldBeTrue();
        empty.Lines.ShouldBeEmpty();
    }

    private static JournalExportDocument Document(params JournalExportLine[] lines) =>
        new("Agence Tests", March, March, null, lines, 1, lines.Select(l => l.Debit).Sum(), lines.Select(l => l.Credit).Sum(), IsFrozen: false, DateTimeOffset.UnixEpoch);
}
