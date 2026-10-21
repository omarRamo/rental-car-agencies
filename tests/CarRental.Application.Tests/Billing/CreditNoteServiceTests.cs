using CarRental.Application.Billing.Invoicing;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Billing.Fakes;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Billing;

/// <summary>Credit notes through the service: the accountant's permission, the lock of the invoice, the limits, the numbers.</summary>
public sealed class CreditNoteServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_accountant_credits_part_of_an_invoice_then_the_rest_never_more()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        var invoice = (await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct)).Value;
        var rental = harness.Db.Rows<Invoice>().Single().Lines[0];
        harness.As(Roles.Accountant);

        var partial = (await harness.Invoicing.CreateCreditNoteAsync(invoice.Id, new CreditNoteRequest(CreditNoteScope.Partial, "Geste commercial", [new CreditLineRequest(rental.Id, 50m)]), Ct)).Value;
        var total = (await harness.Invoicing.CreateCreditNoteAsync(invoice.Id, new CreditNoteRequest(CreditNoteScope.Total, "Annulation du solde"), Ct)).Value;

        (partial.Number, total.Number).ShouldBe(("AV-2026-000001", "AV-2026-000002"));
        harness.Locks.Acquired.ShouldContain(BillingLocks.Invoice(invoice.Id));
        var notes = harness.Db.Rows<CreditNote>();
        notes.Select(n => n.TotalInclVat).Sum().ShouldBe(Money.Tnd(-681.4m));
        Crediting.RemainingOf(harness.Db.Rows<Invoice>().Single(), notes).ShouldBe(Money.Zero);
        notes[0].IssuedBy.ShouldBe("accountant@tests.local");
        notes.ShouldAllBe(n => n.Mentions.Any(m => m.Contains("transitoire", StringComparison.Ordinal)));

        (await harness.Invoicing.CreateCreditNoteAsync(invoice.Id, new CreditNoteRequest(CreditNoteScope.Total, "Encore une fois"), Ct)).Error.ShouldBe(BillingErrors.NothingToCredit);
        (await harness.Invoicing.CreateCreditNoteAsync(Guid.CreateVersion7(), new CreditNoteRequest(CreditNoteScope.Total, "Inconnue"), Ct)).Error.ShouldBe(BillingErrors.InvoiceNotFound);
    }

    [Fact]
    public async Task A_refused_credit_note_gives_its_number_back()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        var invoice = (await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct)).Value;
        harness.As(Roles.Accountant);

        (await harness.Invoicing.CreateCreditNoteAsync(invoice.Id, new CreditNoteRequest(CreditNoteScope.Partial, "Trop", [new CreditLineRequest(harness.Db.Rows<Invoice>().Single().Lines[0].Id, 1_000m)]), Ct))
            .Error.ShouldBe(BillingErrors.CreditNoteAmountInvalid);
        (await harness.Invoicing.CreateCreditNoteAsync(invoice.Id, new CreditNoteRequest(CreditNoteScope.Total, "Erreur de client"), Ct)).Value.Number.ShouldBe("AV-2026-000001");
    }

    [Fact]
    public async Task Only_a_manager_of_invoices_creates_credit_notes()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        var invoice = (await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct)).Value;

        (await harness.As(Roles.Agent).Invoicing.CreateCreditNoteAsync(invoice.Id, new CreditNoteRequest(CreditNoteScope.Total, "Erreur"), Ct)).Error.ShouldBe(BillingErrors.Forbidden);
        (await harness.As(Roles.Manager).Invoicing.CreateCreditNoteAsync(invoice.Id, new CreditNoteRequest(CreditNoteScope.Total, "Erreur"), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task The_page_of_an_invoice_shows_what_remains_creditable_and_what_the_user_may_do()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        var invoice = (await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct)).Value;
        harness.As(Roles.Accountant);
        await harness.Invoicing.CreateCreditNoteAsync(invoice.Id, new CreditNoteRequest(CreditNoteScope.Partial, "Geste", [new CreditLineRequest(harness.Db.Rows<Invoice>().Single().Lines[0].Id, 30m)]), Ct);

        var detail = (await harness.Administration.GetInvoiceAsync(invoice.Id, Ct)).Value;

        detail.Remaining.ShouldBe(Money.Tnd(651.4m));
        detail.Lines[0].RemainingInclVat.ShouldBe(Money.Tnd(300m));
        detail.CreditNotes.ShouldHaveSingleItem().TotalInclVat.ShouldBe(Money.Tnd(-30m));
        detail.Actions.CanCredit.ShouldBeTrue();
        detail.Actions.CanRetryTransmission.ShouldBeFalse();
        detail.Payments.ShouldNotBeNull().Totals.NetPaid.ShouldBe(Money.Tnd(681.4m));
        (await harness.As(Roles.Agent).Administration.GetInvoiceAsync(invoice.Id, Ct)).Value.Actions.CanCredit.ShouldBeFalse();
    }
}
