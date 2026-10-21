using CarRental.Application.Billing.Contracts;
using CarRental.Application.Billing.Invoicing;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Billing.Fakes;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Billing;

/// <summary>
/// The invoicing moments (ADR-0057): the final invoice at the closing of a file — its quote and its return charges, the
/// franchise cap included, the deposit retained settling them without being revenue — one per file under replays and
/// concurrent closings; the invoice of a cancellation fee; nothing issued while the seller identity is incomplete.
/// </summary>
public sealed class InvoicingServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OutboxMessageContext Context = new(Guid.CreateVersion7(), BillingHarness.Now, 1, "corr-outbox");

    [Fact]
    public async Task The_closing_invoices_the_quote_and_the_return_charges_to_the_payer()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        var charges = harness.ChargesWithCappedDamage(file.BookingId);
        harness.Payments.Paid[file.BookingId] = (Money.Tnd(681.4m), charges.TotalInclVat);

        var issued = (await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct)).Value;

        issued.AlreadyIssued.ShouldBeFalse();
        issued.Number.ShouldBe("FAC-2026-000001");
        var invoice = harness.Db.Rows<Invoice>().ShouldHaveSingleItem();
        (invoice.Kind, invoice.BookingId, invoice.BookingNumber).ShouldBe((InvoiceKind.Rental, (Guid?)file.BookingId, file.Number));
        invoice.TotalInclVat.ShouldBe(file.Quote.TotalInclVat + charges.TotalInclVat);
        invoice.Lines.Select(l => l.Nature).ShouldBe([
            InvoiceLineNature.Rental,
            InvoiceLineNature.Rental,
            InvoiceLineNature.Discount,
            InvoiceLineNature.Extras,
            InvoiceLineNature.Extras,
            InvoiceLineNature.DailyContribution,
            InvoiceLineNature.Fuel,
            InvoiceLineNature.DailyContribution,
            InvoiceLineNature.Damage,
            InvoiceLineNature.Damage,
        ]);

        // The contribution lines keep their HT (2.000 per day), the damage is capped by its negative line.
        invoice.Lines.Where(l => l.Nature == InvoiceLineNature.DailyContribution).Select(l => l.ExclVat).ShouldBe([Money.Tnd(10m), Money.Tnd(2m)]);
        invoice.Lines.Where(l => l.Nature == InvoiceLineNature.Damage).Select(l => l.InclVat).Sum().ShouldBe(Money.Tnd(600m));
        invoice.Lines.Single(l => l.Code == "Damage").Description.ShouldBe("ReturnCharge.Label.Damage()[Pare-chocs avant]@fr");

        // The seller and the buyer are frozen; the deposit retained settles the charges and is no line of the invoice.
        (invoice.Seller.LegalName, invoice.Seller.TaxIdentifier, invoice.Seller.Address).ShouldBe(("Agence Tests SARL", "1234567/A/M/000", "1 avenue de la République, Tunis"));
        (invoice.Buyer.Kind, invoice.Buyer.Name, invoice.Buyer.City, invoice.Buyer.Email).ShouldBe((BuyerKind.Individual, "Sami Ben Salah", "Tunis", "sami@tests.local"));
        (invoice.PaidAtIssue, invoice.DepositAppliedAtIssue, invoice.BalanceAtIssue).ShouldBe((Money.Tnd(681.4m), charges.TotalInclVat, Money.Zero));
        invoice.ServiceFromUtc.ShouldBe(file.ActualPickupAt);
        invoice.Mentions.ShouldContain(m => m.Contains("transitoire", StringComparison.Ordinal));
        harness.Locks.Acquired.ShouldBe([BillingLocks.Booking(file.BookingId)]);
        harness.Db.Events.OfType<InvoiceIssued>().ShouldHaveSingleItem().InvoiceId.ShouldBe(invoice.Id);
    }

    [Fact]
    public async Task The_reference_quote_invoices_to_its_totals_and_the_stamp_duty_is_added_outside_the_vat()
    {
        var harness = new BillingHarness();
        harness.Settings.Values[WellKnownSettings.StampDuty.Key] = Money.Tnd(1m);
        var file = harness.ClosedFile();

        await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct);

        var invoice = harness.Db.Rows<Invoice>().ShouldHaveSingleItem();
        (invoice.TotalInclVat, invoice.TotalExclVat, invoice.TotalVat).ShouldBe((Money.Tnd(681.4m), Money.Tnd(572.605m), Money.Tnd(108.795m)));
        (invoice.StampDuty, invoice.TotalDue).ShouldBe((Money.Tnd(1m), Money.Tnd(682.4m)));
        invoice.BalanceAtIssue.ShouldBe(Money.Tnd(1m), "the customer paid the quote: the stamp duty remains due");
    }

    [Fact]
    public async Task Replays_and_concurrent_closings_issue_one_invoice_and_keep_the_numbers_gapless()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct)));
        await harness.InvoicingHandler.HandleAsync(StatusChanged(file, BookingStatusInfo.Closed), Context, Ct);

        attempts.ShouldAllBe(a => a.IsSuccess);
        attempts.Count(a => !a.Value.AlreadyIssued).ShouldBe(1);
        attempts.Select(a => a.Value.Number).Distinct().ShouldHaveSingleItem().ShouldBe("FAC-2026-000001");
        harness.Db.Rows<Invoice>().ShouldHaveSingleItem();

        var next = harness.ClosedFile();
        (await harness.Invoicing.IssueRentalInvoiceAsync(next.BookingId, Ct)).Value.Number.ShouldBe("FAC-2026-000002", "no number was lost to the replays");
    }

    [Fact]
    public async Task A_file_is_invoiced_once_it_is_closed_only()
    {
        var harness = new BillingHarness();
        var returned = harness.ClosedFile(BookingStatusInfo.Returned);

        (await harness.Invoicing.IssueRentalInvoiceAsync(returned.BookingId, Ct)).Error.ShouldBe(BillingErrors.BookingNotClosed);
        (await harness.Invoicing.IssueRentalInvoiceAsync(Guid.CreateVersion7(), Ct)).Error.ShouldBe(BillingErrors.BookingNotFound);
        harness.Db.Rows<Invoice>().ShouldBeEmpty();

        var next = harness.ClosedFile();
        (await harness.Invoicing.IssueRentalInvoiceAsync(next.BookingId, Ct)).Value.Number.ShouldBe("FAC-2026-000001", "a refused issue gives its number back");
    }

    [Fact]
    public async Task The_closing_message_issues_the_invoice_when_the_agency_issues_automatically()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();

        await harness.InvoicingHandler.HandleAsync(StatusChanged(file, BookingStatusInfo.Returned), Context, Ct);
        harness.Db.Rows<Invoice>().ShouldBeEmpty("not closed yet");

        harness.Settings.Values[BillingSettings.IssueAutomatically.Key] = false;
        await harness.InvoicingHandler.HandleAsync(StatusChanged(file, BookingStatusInfo.Closed), Context, Ct);
        harness.Db.Rows<Invoice>().ShouldBeEmpty("an accountant issues it from the list of the files to invoice");

        harness.Settings.Values[BillingSettings.IssueAutomatically.Key] = true;
        await harness.InvoicingHandler.HandleAsync(StatusChanged(file, BookingStatusInfo.Closed), Context, Ct);
        var invoice = harness.Db.Rows<Invoice>().ShouldHaveSingleItem();
        invoice.IssuedBy.ShouldBe(BillingScopes.ActorName);
    }

    [Fact]
    public async Task Without_the_legal_identity_of_the_agency_no_invoice_is_issued_and_the_file_waits()
    {
        var harness = new BillingHarness();
        harness.Referential.Agency = harness.Referential.Agency with { Legal = new AgencyLegalIdentityInfo("Agence Tests SARL", null, null, null, null) };
        var file = harness.ClosedFile();

        (await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct)).Error.ShouldBe(BillingErrors.SellerIdentityIncomplete);
        await Should.NotThrowAsync(() => harness.InvoicingHandler.HandleAsync(StatusChanged(file, BookingStatusInfo.Closed), Context, Ct));

        harness.Db.Rows<Invoice>().ShouldBeEmpty();
        var waiting = (await harness.As(Roles.Accountant).Administration.GetFilesToInvoiceAsync(BillingHarness.AgencyId, Ct)).Value.ShouldHaveSingleItem();
        (waiting.BookingId, waiting.Kind, waiting.BlockingErrorCode).ShouldBe((file.BookingId, InvoiceKind.Rental, BillingErrors.SellerIdentityIncomplete.Code));
    }

    [Fact]
    public async Task A_lock_held_too_long_is_retried_by_the_outbox()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        harness.Locks.Next = BillingErrors.LockTimeout;

        await Should.ThrowAsync<BillingRetryException>(() => harness.InvoicingHandler.HandleAsync(StatusChanged(file, BookingStatusInfo.Closed), Context, Ct));
        await harness.InvoicingHandler.HandleAsync(StatusChanged(file, BookingStatusInfo.Closed), Context, Ct);

        harness.Db.Rows<Invoice>().ShouldHaveSingleItem().Number.ShouldBe("FAC-2026-000001");
    }

    [Fact]
    public async Task A_company_payer_is_invoiced_with_its_tax_identifier_and_billing_address()
    {
        var harness = new BillingHarness();
        var company = new CustomerInfo(
            Guid.CreateVersion7(),
            CustomerKindInfo.Company,
            "Société Cliente SA",
            null,
            "Société Cliente SA",
            "7654321/B/A/000",
            new PostalAddressInfo("Zone industrielle", "Lot 12", "2035", "Charguia", "TN"),
            "factures@cliente.local",
            null);
        harness.Customers.Customers[company.Id] = company;
        var file = harness.ClosedFile(payer: company.Id);

        await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct);

        var buyer = harness.Db.Rows<Invoice>().ShouldHaveSingleItem().Buyer;
        (buyer.Kind, buyer.Name, buyer.TaxIdentifier, buyer.AddressText, buyer.CustomerId)
            .ShouldBe((BuyerKind.Company, "Société Cliente SA", "7654321/B/A/000", "Zone industrielle, Lot 12, 2035 Charguia, TN", (Guid?)company.Id));
    }

    [Fact]
    public async Task A_booking_com_file_is_invoiced_to_its_recorded_payer_with_the_reference_of_the_channel()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile(channel: SalesChannel.BookingCom, channelReference: "BDC-998877");

        await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct);

        var invoice = harness.Db.Rows<Invoice>().ShouldHaveSingleItem();
        invoice.ChannelReference.ShouldBe("BDC-998877");
        invoice.Mentions.ShouldContain("Billing.Mention.ChannelBooking(BookingCom,BDC-998877)@fr");
        invoice.Buyer.CustomerId.ShouldBe(harness.Payer.Id);
    }

    [Fact]
    public async Task A_cancellation_fee_is_invoiced_once_and_a_free_cancellation_has_nothing_to_invoice()
    {
        var harness = new BillingHarness();
        harness.Settings.Values[WellKnownSettings.VatRate.Key] = 0.19m;
        var charged = harness.ClosedFile(BookingStatusInfo.Cancelled, Money.Tnd(136.28m));
        var free = harness.ClosedFile(BookingStatusInfo.Cancelled, Money.Zero);
        var closed = harness.ClosedFile();

        await harness.InvoicingHandler.HandleAsync(StatusChanged(charged, BookingStatusInfo.Cancelled), Context, Ct);
        await harness.InvoicingHandler.HandleAsync(StatusChanged(charged, BookingStatusInfo.Cancelled), Context, Ct);
        await harness.InvoicingHandler.HandleAsync(StatusChanged(free, BookingStatusInfo.Cancelled), Context, Ct);

        var invoice = harness.Db.Rows<Invoice>().ShouldHaveSingleItem();
        invoice.Kind.ShouldBe(InvoiceKind.CancellationFee);
        var line = invoice.Lines.ShouldHaveSingleItem();
        (line.Nature, line.Code, line.InclVat, line.ExclVat, line.VatRate).ShouldBe((InvoiceLineNature.CancellationFee, "CANCELLATION_FEE", Money.Tnd(136.28m), Money.Tnd(114.521m), 0.19m));
        line.Description.ShouldBe($"Billing.Line.CancellationFee({charged.Number})@fr");
        (await harness.Invoicing.IssueCancellationInvoiceAsync(free.BookingId, Ct)).Error.ShouldBe(BillingErrors.NoCancellationFee);
        (await harness.Invoicing.IssueCancellationInvoiceAsync(closed.BookingId, Ct)).Error.ShouldBe(BillingErrors.BookingNotCancelled);
    }

    [Fact]
    public async Task Issuing_needs_the_permission_to_issue_invoices()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();

        (await harness.As(Roles.Preparer).Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct)).Error.ShouldBe(BillingErrors.Forbidden);
        (await harness.As(Roles.Agent).Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct)).IsSuccess.ShouldBeTrue();
    }

    private static BookingStatusChanged StatusChanged(BookingBillingInfo file, BookingStatusInfo status) =>
        new(file.BookingId, file.AgencyId, file.Number, file.Channel, BookingStatusInfo.Returned, status, file.Quote.TotalInclVat, null, BillingHarness.Now);
}
