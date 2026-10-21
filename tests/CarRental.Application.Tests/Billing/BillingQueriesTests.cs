using CarRental.Application.Billing.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Application.Tests.Billing.Fakes;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Billing;

/// <summary>
/// What the Billing module gives the other modules: the sales journal of package 3B (documents with lines, natures, VAT per
/// rate), the invoices of a file, the open amounts per customer, and the amounts invoiced of the Reporting port.
/// </summary>
public sealed class BillingQueriesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_sales_journal_lists_the_invoices_then_the_credit_notes_with_their_lines_and_vat_per_rate()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        harness.ChargesWithCappedDamage(file.BookingId);
        var invoice = (await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct)).Value;
        harness.As(Roles.Accountant);
        var line = harness.Db.Rows<Invoice>().Single().Lines[0];
        await harness.Invoicing.CreateCreditNoteAsync(invoice.Id, new CreditNoteRequest(CreditNoteScope.Partial, "Geste", [new CreditLineRequest(line.Id, 30m)]), Ct);

        var journal = await harness.Queries.GetSalesDocumentsAsync(BillingHarness.AgencyId, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), Ct);

        journal.Select(d => (d.Type, d.Number)).ShouldBe([(SalesDocumentTypeInfo.Invoice, "FAC-2026-000001"), (SalesDocumentTypeInfo.CreditNote, "AV-2026-000001")]);
        var sold = journal[0];
        (sold.InvoiceKind, sold.BookingNumber, sold.Buyer.Name, sold.Buyer.Kind).ShouldBe(((InvoiceKindInfo?)InvoiceKindInfo.Rental, file.Number, "Sami Ben Salah", BuyerKindInfo.Individual));
        sold.Lines.Select(l => l.Nature).ShouldContain(InvoiceLineNatureInfo.DailyContribution);
        sold.Lines.Select(l => l.Nature).ShouldContain(InvoiceLineNatureInfo.Damage);
        sold.VatTotals.ShouldHaveSingleItem().InclVat.ShouldBe(sold.TotalInclVat);
        sold.Lines.Select(l => l.InclVat).Sum().ShouldBe(sold.TotalInclVat);
        sold.Payments.ShouldNotBeNull().PaidAtIssue.ShouldBe(Money.Tnd(681.4m));
        sold.EInvoice.ShouldBe(new EInvoiceInfo(EInvoiceModeInfo.PaperTransitional, EInvoiceStatusInfo.NotRequired, null, null));

        var credit = journal[1];
        (credit.InvoiceId, credit.InvoiceNumber, credit.CreditReason, credit.TotalInclVat).ShouldBe(((Guid?)invoice.Id, "FAC-2026-000001", "Geste", Money.Tnd(-30m)));
        credit.Lines.ShouldHaveSingleItem().InvoiceLineId.ShouldBe(line.Id);
        (await harness.Queries.GetSalesDocumentsAsync(BillingHarness.AgencyId, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), Ct)).ShouldBeEmpty();
        (await harness.Queries.GetDocumentAsync(invoice.Id, Ct)).ShouldNotBeNull().Number.ShouldBe("FAC-2026-000001");
    }

    [Fact]
    public async Task The_invoices_of_a_file_show_what_their_credit_notes_took_back()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        var invoice = (await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct)).Value;
        harness.As(Roles.Accountant);
        await harness.Invoicing.CreateCreditNoteAsync(invoice.Id, new CreditNoteRequest(CreditNoteScope.Total, "Erreur"), Ct);

        var invoices = await harness.Queries.GetInvoicesOfBookingAsync(file.BookingId, Ct);

        var summary = invoices.ShouldHaveSingleItem();
        (summary.Credited, summary.Remaining).ShouldBe((Money.Tnd(-681.4m), Money.Zero));
        summary.CreditNotes.ShouldHaveSingleItem().Number.ShouldBe("AV-2026-000001");
    }

    [Fact]
    public async Task The_open_amount_of_a_customer_is_what_was_invoiced_less_credit_notes_and_what_was_paid()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        harness.Payments.Paid[file.BookingId] = (Money.Tnd(600m), Money.Zero);
        await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct);

        var open = (await harness.Queries.GetOpenAmountsAsync(BillingHarness.AgencyId, Ct)).ShouldHaveSingleItem();

        (open.CustomerId, open.BuyerName, open.Invoiced, open.Settled, open.Open).ShouldBe(((Guid?)harness.Payer.Id, "Sami Ben Salah", Money.Tnd(681.4m), Money.Tnd(600m), Money.Tnd(81.4m)));
    }

    [Fact]
    public async Task The_amounts_invoiced_of_a_period_are_the_invoices_less_the_credit_notes_without_the_stamp_duty()
    {
        var harness = new BillingHarness();
        harness.Settings.Values[WellKnownSettings.StampDuty.Key] = Money.Tnd(1m);
        var first = harness.ClosedFile();
        var second = harness.ClosedFile();
        var invoice = (await harness.Invoicing.IssueRentalInvoiceAsync(first.BookingId, Ct)).Value;
        await harness.Invoicing.IssueRentalInvoiceAsync(second.BookingId, Ct);
        harness.As(Roles.Accountant);
        await harness.Invoicing.CreateCreditNoteAsync(invoice.Id, new CreditNoteRequest(CreditNoteScope.Total, "Erreur"), Ct);

        var invoiced = await harness.InvoicedAmounts.GetInvoicedAsync(BillingHarness.AgencyId, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), Ct);

        invoiced.ShouldBe(new InvoicedAmountsInfo(Money.Tnd(572.605m), Money.Tnd(108.795m), Money.Tnd(681.4m), 2, 1));
        (await harness.InvoicedAmounts.GetInvoicedAsync(BillingHarness.AgencyId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), Ct)).ShouldBe(new InvoicedAmountsInfo(Money.Zero, Money.Zero, Money.Zero, 0, 0));
    }
}
