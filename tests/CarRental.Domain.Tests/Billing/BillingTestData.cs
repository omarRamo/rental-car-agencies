using System.Globalization;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Billing;

/// <summary>Parties, lines and documents of the billing tests.</summary>
internal static class BillingTestData
{
    public const decimal Rate = 0.19m;

    public static readonly Guid AgencyId = Guid.CreateVersion7();

    public static readonly BillingActor Accountant = new(Guid.CreateVersion7(), "comptable@tests.local");

    /// <summary>Noon in Tunis on 2026-11-02 (UTC+1).</summary>
    public static readonly DateTimeOffset IssuedAt = new(2026, 11, 2, 11, 0, 0, TimeSpan.Zero);

    public static SellerIdentity Seller() =>
        SellerIdentity.Create("Location Tests SARL", "1234567/A/M/000", "B0123456789", "12 rue de la Liberté, 1002 Tunis", "+216 71 000 000", "contact@tests.local", "Capital 10 000 TND").Value;

    public static BuyerIdentity Person() =>
        BuyerIdentity.Create(BuyerKind.Individual, Guid.CreateVersion7(), "Sami Ben Salah", null, "5 avenue Habib Bourguiba", null, "1000", "Tunis", "TN", "sami@tests.local").Value;

    public static BuyerIdentity Company() =>
        BuyerIdentity.Create(BuyerKind.Company, Guid.CreateVersion7(), "Société Cliente SA", "7654321/B/A/000", "Zone industrielle", null, "2035", "Charguia", "TN", "factures@cliente.local").Value;

    /// <summary>A line entered VAT included at 19 % (or <paramref name="rate"/>).</summary>
    public static InvoiceLineInput Line(decimal inclVat, InvoiceLineNature nature = InvoiceLineNature.Rental, int quantity = 1, decimal rate = Rate, string code = "RENTAL") =>
        new(
            nature,
            code,
            "Quote.Line.Rental.Base",
            [],
            null,
            $"Ligne {inclVat.ToString(CultureInfo.InvariantCulture)}",
            quantity,
            Money.Tnd(quantity == 0 ? inclVat : inclVat / quantity),
            Money.Tnd(inclVat),
            rate);

    /// <summary>The daily contribution of <paramref name="days"/> days: 2.000 HT per day, VAT added.</summary>
    public static InvoiceLineInput Contribution(int days, decimal rate = Rate)
    {
        var exclVat = Money.Tnd(2m) * days;
        var vat = exclVat * rate;
        return new InvoiceLineInput(
            InvoiceLineNature.DailyContribution,
            "DAILY_CONTRIBUTION",
            "Quote.Line.DailyContribution",
            ["2.000"],
            null,
            "Contribution journalière",
            days,
            Money.Tnd(2m) * (1m + rate),
            exclVat + vat,
            rate,
            AmountBasis.ExclVat,
            exclVat);
    }

    /// <summary>
    /// The lines of the reference quote of PROMPT §7 (ECMR, 28/06/2027 to 03/07/2027): 3 days at 110, 2 days at 140, tier
    /// −5 %, additional driver and child seat, daily contribution — 681.400 TTC, 572.605 HT, 108.795 VAT.
    /// </summary>
    public static IReadOnlyList<InvoiceLineInput> ReferenceQuote() =>
    [
        Line(330m, quantity: 3),
        Line(280m, quantity: 2),
        Line(-30.5m, InvoiceLineNature.Discount, code: "DURATION_DISCOUNT"),
        Line(50m, InvoiceLineNature.Extras, quantity: 5, code: "ADDITIONAL_DRIVER"),
        Line(40m, InvoiceLineNature.Extras, quantity: 5, code: "CHILD_SEAT"),
        Contribution(5),
    ];

    public static InvoiceDraftRequest Request(
        IReadOnlyList<InvoiceLineInput> lines,
        decimal stampDuty = 0m,
        BuyerIdentity? buyer = null,
        InvoicePayments? payments = null,
        IReadOnlyList<string>? mentions = null,
        InvoiceKind kind = InvoiceKind.Rental) =>
        new(
            AgencyId,
            kind,
            Seller(),
            buyer ?? Person(),
            new InvoiceSource(Guid.CreateVersion7(), "LOC-2026-000123", null, IssuedAt.AddDays(-5), IssuedAt.AddHours(-2)),
            lines,
            Money.Tnd(stampDuty),
            payments ?? InvoicePayments.None,
            mentions ?? ["Facture établie sur support papier à titre transitoire."]);

    public static InvoiceDraft Draft(IReadOnlyList<InvoiceLineInput> lines, decimal stampDuty = 0m) => InvoiceDraft.Create(Request(lines, stampDuty)).Value;

    public static DocumentNumber InvoiceNumber(long sequence = 45, int year = 2026) => DocumentNumber.Create(DocumentType.Invoice, year, sequence).Value;

    public static DocumentNumber CreditNoteNumber(long sequence = 3, int year = 2026) => DocumentNumber.Create(DocumentType.CreditNote, year, sequence).Value;

    public static Invoice Issued(IReadOnlyList<InvoiceLineInput> lines, decimal stampDuty = 0m, EInvoiceMode mode = EInvoiceMode.PaperTransitional) =>
        Invoice.Issue(Draft(lines, stampDuty), InvoiceNumber(), IssuedAt, Accountant, mode).Value;

    public static Result<CreditNote> Credit(Invoice invoice, IReadOnlyCollection<CreditNote> existing, CreditNoteRequest request, long sequence = 3)
    {
        var draft = CreditNoteDraft.Create(invoice, existing, request, Seller(), ["Avoir sur facture."]);
        return draft.IsFailure ? draft.Error : CreditNote.Issue(draft.Value, CreditNoteNumber(sequence), IssuedAt.AddDays(1), Accountant, EInvoiceMode.PaperTransitional);
    }
}
