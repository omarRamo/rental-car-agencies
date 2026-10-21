using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Billing.BillingTestData;

namespace CarRental.Domain.Tests.Billing;

/// <summary>
/// An invoice (PROMPT §6 invariant 6): numbered FAC-AAAA-NNNNNN in the fiscal year of its Tunis issue date, frozen once
/// issued — only its TTN state, its PDF reference and its e-mail follow, each once.
/// </summary>
public sealed class InvoiceTests
{
    [Fact]
    public void An_issued_invoice_carries_its_number_its_tunis_issue_date_its_parties_and_its_frozen_lines()
    {
        var invoice = Issued(ReferenceQuote(), stampDuty: 1m);

        invoice.Number.ShouldBe("FAC-2026-000045");
        (invoice.FiscalYear, invoice.Sequence).ShouldBe((2026, 45L));
        invoice.IssueDate.ShouldBe(new DateOnly(2026, 11, 2));
        invoice.IssuedBy.ShouldBe(Accountant.Name);
        invoice.Seller.TaxIdentifier.ShouldBe("1234567/A/M/000");
        invoice.Buyer.Name.ShouldBe("Sami Ben Salah");
        invoice.Lines.Select(l => l.Position).ShouldBe([1, 2, 3, 4, 5, 6]);
        invoice.Lines.Sum(l => l.InclVat.Amount).ShouldBe(681.4m);
        (invoice.TotalExclVat, invoice.TotalVat, invoice.TotalInclVat, invoice.StampDuty, invoice.TotalDue)
            .ShouldBe((Money.Tnd(572.605m), Money.Tnd(108.795m), Money.Tnd(681.4m), Money.Tnd(1m), Money.Tnd(682.4m)));
        invoice.Lines[^1].ShouldSatisfyAllConditions(
            l => l.Nature.ShouldBe(InvoiceLineNature.DailyContribution),
            l => l.Basis.ShouldBe(AmountBasis.ExclVat),
            l => l.UnitPriceExclVat.ShouldBe(Money.Tnd(2m)));
        invoice.Mentions.ShouldHaveSingleItem().ShouldContain("transitoire");
        invoice.PdfFileName.ShouldBe("FAC-2026-000045.pdf");
        BillingDocument.PdfPathOf(invoice.FiscalYear, invoice.Number).ShouldBe("2026/FAC-2026-000045.pdf");
        invoice.DomainEvents.OfType<InvoiceIssued>().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.Number.ShouldBe(invoice.Number),
            e => e.TotalDue.ShouldBe(Money.Tnd(682.4m)),
            e => e.EInvoiceMode.ShouldBe(EInvoiceMode.PaperTransitional));
    }

    [Fact]
    public void The_fiscal_year_is_the_tunis_year_of_the_issue()
    {
        // 23:30 UTC on 31 December 2026 is already 00:30 on 1 January 2027 in Tunis.
        var newYearsEve = new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.Zero);

        Invoice.Issue(Draft(ReferenceQuote()), InvoiceNumber(1, 2026), newYearsEve, Accountant, EInvoiceMode.PaperTransitional)
            .Error.ShouldBe(BillingErrors.NumberMismatch);
        var invoice = Invoice.Issue(Draft(ReferenceQuote()), InvoiceNumber(1, 2027), newYearsEve, Accountant, EInvoiceMode.PaperTransitional).Value;
        invoice.IssueDate.ShouldBe(new DateOnly(2027, 1, 1));
        invoice.Number.ShouldBe("FAC-2027-000001");
    }

    [Fact]
    public void A_credit_note_number_cannot_number_an_invoice()
    {
        Invoice.Issue(Draft(ReferenceQuote()), CreditNoteNumber(), IssuedAt, Accountant, EInvoiceMode.PaperTransitional).Error.ShouldBe(BillingErrors.NumberMismatch);
    }

    [Fact]
    public void A_draft_needs_lines_with_amounts_a_known_rate_and_a_total_that_is_not_negative()
    {
        InvoiceDraft.Create(Request([])).Error.ShouldBe(BillingErrors.InvoiceNoLine);
        InvoiceDraft.Create(Request([Line(0m)])).Error.ShouldBe(BillingErrors.InvoiceLineInvalid);
        InvoiceDraft.Create(Request([Line(10m, rate: 1.5m)])).Error.ShouldBe(BillingErrors.InvoiceLineInvalid);
        InvoiceDraft.Create(Request([Line(10m) with { Description = " " }])).Error.ShouldBe(BillingErrors.InvoiceLineInvalid);
        InvoiceDraft.Create(Request([Line(10m), Line(-20m, InvoiceLineNature.Discount)])).Error.ShouldBe(BillingErrors.InvoiceNegativeTotal);
        InvoiceDraft.Create(Request([.. Enumerable.Range(0, Invoice.MaxLines + 1).Select(_ => Line(1m))])).Error.ShouldBe(BillingErrors.InvoiceTooManyLines);
        InvoiceDraft.Create(Request([Line(10m)], mentions: [""])).Error.ShouldBe(BillingErrors.MentionInvalid);
        InvoiceDraft.Create(Request([Line(10m)], stampDuty: -1m)).Error.ShouldBe(BillingErrors.InvoiceLineInvalid);
    }

    [Fact]
    public void A_line_entered_excluding_vat_must_give_a_coherent_amount_excluding_vat()
    {
        var contribution = Contribution(2);

        (contribution with { ExclVat = null }).IsValid().ShouldBeFalse();
        (contribution with { ExclVat = Money.Tnd(-4m) }).IsValid().ShouldBeFalse();
        (contribution with { ExclVat = Money.Tnd(10m) }).IsValid().ShouldBeFalse("more than the amount VAT included");
        (Line(10m) with { ExclVat = Money.Tnd(8m) }).IsValid().ShouldBeFalse("a line entered VAT included has no HT of its own");
        contribution.IsValid().ShouldBeTrue();
    }

    [Fact]
    public void What_was_paid_and_retained_from_the_deposit_leaves_the_balance_at_the_issue()
    {
        var draft = InvoiceDraft.Create(Request(ReferenceQuote(), stampDuty: 1m, payments: new InvoicePayments(Money.Tnd(600m), Money.Tnd(81.4m)))).Value;
        var invoice = Invoice.Issue(draft, InvoiceNumber(), IssuedAt, Accountant, EInvoiceMode.PaperTransitional).Value;

        (invoice.PaidAtIssue, invoice.DepositAppliedAtIssue, invoice.BalanceAtIssue).ShouldBe((Money.Tnd(600m), Money.Tnd(81.4m), Money.Tnd(1m)));
    }

    [Fact]
    public void A_paper_invoice_needs_no_transmission_and_its_pdf_can_be_made_at_once()
    {
        var invoice = Issued(ReferenceQuote());

        invoice.EInvoiceStatus.ShouldBe(EInvoiceStatus.NotRequired);
        invoice.IsReadyForPdf.ShouldBeTrue();
        invoice.RecordTransmission("TTN-1", IssuedAt).Error.ShouldBe(BillingErrors.EInvoiceNotTtn);
        invoice.RecordTransmissionFailure("boom", IssuedAt).Error.ShouldBe(BillingErrors.EInvoiceNotTtn);
    }

    [Fact]
    public void A_ttn_invoice_waits_for_its_reference_records_its_failures_and_keeps_its_reference_once()
    {
        var invoice = Issued(ReferenceQuote(), mode: EInvoiceMode.Ttn);
        invoice.EInvoiceStatus.ShouldBe(EInvoiceStatus.Pending);
        invoice.IsReadyForPdf.ShouldBeFalse("the PDF prints the TTN reference");

        invoice.RecordTransmissionFailure("billing.einvoice.not_configured: TEIF schema not delivered", IssuedAt.AddMinutes(1)).IsSuccess.ShouldBeTrue();
        (invoice.EInvoiceStatus, invoice.TransmissionAttempts).ShouldBe((EInvoiceStatus.Failed, 1));
        invoice.LastTransmissionError.ShouldStartWith("billing.einvoice.not_configured");

        invoice.RecordTransmission("TTN-2026-ABC", IssuedAt.AddMinutes(5)).IsSuccess.ShouldBeTrue();
        (invoice.EInvoiceStatus, invoice.TtnReference, invoice.TransmissionAttempts, invoice.LastTransmissionError)
            .ShouldBe((EInvoiceStatus.Transmitted, "TTN-2026-ABC", 2, (string?)null));
        invoice.IsReadyForPdf.ShouldBeTrue();
        invoice.RecordTransmission("TTN-2026-ABC", IssuedAt.AddMinutes(6)).IsSuccess.ShouldBeTrue("a replay of the same answer");
        invoice.RecordTransmission("TTN-OTHER", IssuedAt.AddMinutes(6)).Error.ShouldBe(BillingErrors.EInvoiceAlreadyTransmitted);
        invoice.RecordTransmissionFailure("late failure", IssuedAt.AddMinutes(7)).Error.ShouldBe(BillingErrors.EInvoiceAlreadyTransmitted);
        invoice.RecordTransmission(" ", IssuedAt).Error.ShouldBe(BillingErrors.EInvoiceReferenceInvalid);
        invoice.DomainEvents.OfType<BillingDocumentTransmitted>().ShouldHaveSingleItem().Reference.ShouldBe("TTN-2026-ABC");
    }

    [Fact]
    public void The_pdf_and_the_email_of_an_invoice_are_recorded_once()
    {
        var invoice = Issued(ReferenceQuote());
        var hash = new string('a', BillingDocument.PdfHashLength);

        invoice.AttachPdf("2026/FAC-2026-000045.pdf", "short", 10, IssuedAt).Error.ShouldBe(BillingErrors.PdfInvalid);
        invoice.AttachPdf("2026/FAC-2026-000045.pdf", hash, 1_234, IssuedAt).IsSuccess.ShouldBeTrue();
        invoice.AttachPdf("2026/FAC-2026-000045-bis.pdf", hash, 1_234, IssuedAt).Error.ShouldBe(BillingErrors.PdfAlreadyStored);
        (invoice.PdfPath, invoice.PdfLength).ShouldBe(("2026/FAC-2026-000045.pdf", 1_234L));

        invoice.MarkEmailed("not-an-address", IssuedAt).Error.ShouldBe(BillingErrors.EmailInvalid);
        invoice.MarkEmailed("sami@tests.local", IssuedAt).IsSuccess.ShouldBeTrue();
        invoice.MarkEmailed("sami@tests.local", IssuedAt).Error.ShouldBe(BillingErrors.AlreadyEmailed);
        invoice.EmailedTo.ShouldBe("sami@tests.local");
    }

    [Fact]
    public void The_vat_totals_of_an_issued_invoice_are_computed_from_its_lines()
    {
        var invoice = Issued([Line(60m, InvoiceLineNature.FineReinvoicing, rate: 0m, code: "FINE"), Line(23.8m, InvoiceLineNature.ManagementFee)]);

        invoice.VatBreakdown().ShouldBe([
            new VatRateTotal(0m, Money.Tnd(60m), Money.Zero, Money.Tnd(60m)),
            new VatRateTotal(Rate, Money.Tnd(20m), Money.Tnd(3.8m), Money.Tnd(23.8m)),
        ]);
    }
}
