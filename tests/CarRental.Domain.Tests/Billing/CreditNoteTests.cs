using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Billing.BillingTestData;

namespace CarRental.Domain.Tests.Billing;

/// <summary>
/// Credit notes (avoirs): numbered AV-AAAA-NNNNNN, a mandatory reason, negative lines tied to the invoice lines, total or
/// partial, never more than what remains creditable — per line and on the whole invoice.
/// </summary>
public sealed class CreditNoteTests
{
    [Fact]
    public void A_total_credit_note_cancels_the_invoice_line_by_line_and_to_the_millime()
    {
        var invoice = Issued(ReferenceQuote(), stampDuty: 1m);

        var note = Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Total, "Facture établie au mauvais client")).Value;

        note.Number.ShouldBe("AV-2026-000003");
        (note.InvoiceId, note.InvoiceNumber, note.Scope).ShouldBe((invoice.Id, invoice.Number, CreditNoteScope.Total));
        note.Buyer.ShouldBe(invoice.Buyer);
        note.Lines.Select(l => l.InvoiceLineId).ShouldBe(invoice.Lines.Select(l => l.Id));
        note.Lines.Zip(invoice.Lines).ShouldAllBe(pair => pair.First.InclVat == -pair.Second.InclVat
                                                          && pair.First.ExclVat == -pair.Second.ExclVat
                                                          && pair.First.Vat == -pair.Second.Vat);
        (note.TotalExclVat, note.TotalVat, note.TotalInclVat).ShouldBe((Money.Tnd(-572.605m), Money.Tnd(-108.795m), Money.Tnd(-681.4m)));
        note.StampDuty.ShouldBe(Money.Zero, "TODO(spec): no stamp duty on a credit note");
        Crediting.RemainingOf(invoice, [note]).ShouldBe(Money.Zero);
        note.DomainEvents.OfType<CreditNoteIssued>().ShouldHaveSingleItem().TotalInclVat.ShouldBe(Money.Tnd(-681.4m));

        Credit(invoice, [note], new CreditNoteRequest(CreditNoteScope.Total, "Encore")).Error.ShouldBe(BillingErrors.NothingToCredit);
    }

    [Fact]
    public void An_invoice_a_credit_note_and_their_lines_belong_to_the_agency_of_the_invoice()
    {
        var invoice = Issued(ReferenceQuote());

        var note = Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Total, "Facture établie au mauvais client")).Value;

        (invoice.AgencyId, note.AgencyId).ShouldBe((AgencyId, AgencyId));
        invoice.Lines.ShouldAllBe(l => l.AgencyId == AgencyId);
        note.Lines.ShouldAllBe(l => l.AgencyId == AgencyId);
        note.DomainEvents.OfType<CreditNoteIssued>().ShouldHaveSingleItem().AgencyId.ShouldBe(AgencyId);
    }

    [Fact]
    public void A_credit_note_states_its_reason()
    {
        var invoice = Issued(ReferenceQuote());

        Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Total, null)).Error.ShouldBe(BillingErrors.CreditNoteReasonRequired);
        Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Total, "  ok ")).Error.ShouldBe(BillingErrors.CreditNoteReasonRequired);
        Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Total, new string('x', CreditNoteDraft.ReasonMaxLength + 1))).Error.ShouldBe(BillingErrors.CreditNoteReasonRequired);
    }

    [Fact]
    public void Partial_credit_notes_credit_parts_of_lines_split_like_them_until_nothing_remains()
    {
        var invoice = Issued([Line(100m), Line(40m, InvoiceLineNature.Extras, code: "GPS")]);
        var rental = invoice.Lines[0];

        // 30.000 of the 100.000 rental line: HT proportional to the line's (84.034 / 100).
        var first = Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Partial, "Geste commercial", [new CreditLineRequest(rental.Id, 30m)])).Value;
        var credited = first.Lines.ShouldHaveSingleItem();
        (credited.InclVat, credited.ExclVat, credited.Vat).ShouldBe((Money.Tnd(-30m), Money.Tnd(-25.21m), Money.Tnd(-4.79m)));
        credited.Description.ShouldBe(rental.Description);
        credited.Nature.ShouldBe(InvoiceLineNature.Rental);

        // The rest of the line closes it exactly, whatever the rounding of the first part.
        var second = Credit(invoice, [first], new CreditNoteRequest(CreditNoteScope.Partial, "Solde du geste", [new CreditLineRequest(rental.Id, 70m)]), sequence: 4).Value;
        var lines = Crediting.LinesOf(invoice, [first, second]);
        lines[0].Remaining.ShouldBe(new LineAmounts(Money.Zero, Money.Zero, Money.Zero));
        lines[1].Remaining.InclVat.ShouldBe(Money.Tnd(40m));

        // Nothing is left on the rental line; the total credit note takes what remains: the GPS.
        Credit(invoice, [first, second], new CreditNoteRequest(CreditNoteScope.Partial, "Trop", [new CreditLineRequest(rental.Id, 0.001m)]), sequence: 5)
            .Error.ShouldBe(BillingErrors.CreditNoteAmountInvalid);
        var rest = Credit(invoice, [first, second], new CreditNoteRequest(CreditNoteScope.Total, "Annulation du reste"), sequence: 5).Value;
        rest.Lines.ShouldHaveSingleItem().InclVat.ShouldBe(Money.Tnd(-40m));
        Crediting.RemainingOf(invoice, [first, second, rest]).ShouldBe(Money.Zero);
        Crediting.LinesOf(invoice, [first, second, rest]).ShouldAllBe(l => l.Remaining.ExclVat.IsZero && l.Remaining.Vat.IsZero && l.Remaining.InclVat.IsZero);
    }

    [Fact]
    public void A_partial_credit_note_credits_known_positive_lines_once_with_amounts_in_millimes()
    {
        var invoice = Issued([Line(100m), Line(-10m, InvoiceLineNature.Discount, code: "PROMOTION")]);
        var rental = invoice.Lines[0];
        var discount = invoice.Lines[1];

        Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Partial, "Sans ligne", [])).Error.ShouldBe(BillingErrors.CreditNoteNoLine);
        Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Partial, "Inconnue", [new CreditLineRequest(Guid.CreateVersion7(), 1m)])).Error.ShouldBe(BillingErrors.CreditNoteLineUnknown);
        Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Partial, "Deux fois", [new CreditLineRequest(rental.Id, 1m), new CreditLineRequest(rental.Id, 1m)]))
            .Error.ShouldBe(BillingErrors.CreditNoteLineUnknown);
        Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Partial, "Remise", [new CreditLineRequest(discount.Id, 5m)])).Error.ShouldBe(BillingErrors.CreditNoteLineNotCreditable);
        Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Partial, "Millimes", [new CreditLineRequest(rental.Id, 1.0005m)])).Error.ShouldBe(BillingErrors.CreditNoteAmountInvalid);
        Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Partial, "Zéro", [new CreditLineRequest(rental.Id, 0m)])).Error.ShouldBe(BillingErrors.CreditNoteAmountInvalid);
        Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Partial, "Plus que la ligne", [new CreditLineRequest(rental.Id, 100.001m)])).Error.ShouldBe(BillingErrors.CreditNoteAmountInvalid);
    }

    [Fact]
    public void A_partial_credit_note_never_takes_more_than_what_remains_of_the_whole_invoice()
    {
        // Damages of 1 000.000 capped at a franchise of 500.000: the invoice is worth 500.000 only.
        var invoice = Issued([Line(1_000m, InvoiceLineNature.Damage, code: "Damage"), Line(-500m, InvoiceLineNature.Damage, code: "FranchiseCap")]);
        var damage = invoice.Lines[0];

        Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Partial, "Réparation moins chère", [new CreditLineRequest(damage.Id, 600m)]))
            .Error.ShouldBe(BillingErrors.CreditNoteExceedsInvoice);
        var note = Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Partial, "Réparation offerte", [new CreditLineRequest(damage.Id, 500m)])).Value;
        Crediting.RemainingOf(invoice, [note]).ShouldBe(Money.Zero);
        Credit(invoice, [note], new CreditNoteRequest(CreditNoteScope.Total, "Le reste"), sequence: 4).Error.ShouldBe(BillingErrors.NothingToCredit);
    }

    [Fact]
    public void A_total_credit_note_after_partial_ones_takes_the_remainders_including_the_negative_lines()
    {
        var invoice = Issued([Line(300m), Line(1_000m, InvoiceLineNature.Damage, code: "Damage"), Line(-500m, InvoiceLineNature.Damage, code: "FranchiseCap")]);
        var first = Credit(invoice, [], new CreditNoteRequest(CreditNoteScope.Partial, "Geste", [new CreditLineRequest(invoice.Lines[0].Id, 200m)])).Value;

        var total = Credit(invoice, [first], new CreditNoteRequest(CreditNoteScope.Total, "Annulation"), sequence: 4).Value;

        total.Lines.Select(l => l.InclVat).ShouldBe([Money.Tnd(-100m), Money.Tnd(-1_000m), Money.Tnd(500m)]);
        total.TotalInclVat.ShouldBe(Money.Tnd(-600m));
        Crediting.RemainingOf(invoice, [first, total]).ShouldBe(Money.Zero);
    }

    [Fact]
    public void A_credit_note_is_numbered_in_the_tunis_year_of_its_issue_with_a_credit_note_number()
    {
        var invoice = Issued(ReferenceQuote());
        var draft = CreditNoteDraft.Create(invoice, [], new CreditNoteRequest(CreditNoteScope.Total, "Erreur de facturation"), Seller(), []).Value;

        CreditNote.Issue(draft, InvoiceNumber(), IssuedAt, Accountant, EInvoiceMode.PaperTransitional).Error.ShouldBe(BillingErrors.NumberMismatch);
        CreditNote.Issue(draft, CreditNoteNumber(1, 2027), IssuedAt, Accountant, EInvoiceMode.PaperTransitional).Error.ShouldBe(BillingErrors.NumberMismatch);
        var ttn = CreditNote.Issue(draft, CreditNoteNumber(1, 2026), IssuedAt, Accountant, EInvoiceMode.Ttn).Value;
        ttn.EInvoiceStatus.ShouldBe(EInvoiceStatus.Pending);
        ttn.BookingNumber.ShouldBe(invoice.BookingNumber);
        ttn.VatBreakdown().ShouldHaveSingleItem().InclVat.ShouldBe(Money.Tnd(-681.4m));
    }
}
