using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using CarRental.Application.Billing.Administration;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Billing.Invoicing;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Billing.BillingWorld;
using static CarRental.Integration.Tests.Bookings.BookingWorld;
using static CarRental.Integration.Tests.Bookings.Handover.HandoverWorld;

namespace CarRental.Integration.Tests.Billing;

/// <summary>
/// Invoices and credit notes on SQL Server and Azurite (ADR-0057, ADR-0058): the invoice of a file closed through the
/// hand-overs, issued once by the outbox with the totals of its quote and of its charges, its PDF stored in the private
/// container <c>invoices</c> and e-mailed; gapless numbers under parallel issues; credit notes never beyond the invoice;
/// the PDF served to the readers of invoices only; an agency on TTN through the provider; the Reporting source.
/// </summary>
public sealed partial class BillingIntegrationTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_file_closed_through_the_handovers_gets_one_invoice_with_its_quote_and_charges_stored_as_pdf_and_emailed()
    {
        var file = await CloseThroughTheHandoversAsync(host, Ct, new Dictionary<string, string> { ["billing.stamp_duty"] = "1.000" });

        // The outbox issues the invoice at the closing, then stores its PDF and queues its e-mail.
        var invoice = await InvoiceOfAsync(host, file.BookingId, i => i.PdfPath is not null && i.EmailedAtUtc is not null, Ct);
        var charges = await RunAsync<IHandoverQueries, ReturnChargesInfo>(host, Accountant, q => q.GetReturnChargesAsync(file.BookingId, Ct));
        var year = TunisTime.DateOf(invoice.IssuedAtUtc).Year;

        InvoiceNumber().IsMatch(invoice.Number).ShouldBeTrue(invoice.Number);
        invoice.Number.ShouldStartWith($"FAC-{year}-");
        invoice.FiscalYear.ShouldBe(year);
        invoice.BookingNumber.ShouldBe(file.Number);
        invoice.TotalInclVat.ShouldBe(file.Total + charges.TotalInclVat, "the invoice totals what the quote and the return charges total");
        (invoice.TotalExclVat + invoice.TotalVat).ShouldBe(invoice.TotalInclVat);
        invoice.Lines.Select(l => l.InclVat).Sum().ShouldBe(invoice.TotalInclVat);
        invoice.StampDuty.ShouldBe(Money.Tnd(1m));
        invoice.TotalDue.ShouldBe(invoice.TotalInclVat + Money.Tnd(1m), "the stamp duty is outside the VAT");
        invoice.PaidAtIssue.ShouldBe(file.Total, "paid at the card terminal");
        invoice.DepositAppliedAtIssue.ShouldBe(charges.TotalInclVat, "the charges were retained from the deposit");
        invoice.BalanceAtIssue.ShouldBe(invoice.StampDuty);
        invoice.EInvoiceMode.ShouldBe(EInvoiceMode.PaperTransitional);
        invoice.EInvoiceStatus.ShouldBe(EInvoiceStatus.NotRequired);
        invoice.Mentions.ShouldContain(m => m.Contains("transitoire", StringComparison.Ordinal));
        invoice.IssuedBy.ShouldStartWith("system:");

        // One invoice per file: an accountant asking again gets the same one.
        var again = Ok(await RunAsync<InvoicingService, Result<IssuedDocument>>(host, Accountant, s => s.IssueRentalInvoiceAsync(file.BookingId, Ct)));
        (again.Id, again.AlreadyIssued).ShouldBe((invoice.Id, true));
        await using (var scope = host.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Invoice>().CountAsync(i => i.BookingId == file.BookingId, Ct)).ShouldBe(1);
        }

        // The PDF in the private container, by year and number.
        invoice.PdfPath.ShouldBe(BillingDocument.PdfPathOf(year, invoice.Number, invoice.Id));
        var pdf = await ReadAsync(host, FileContainers.Invoices, invoice.PdfPath!, Ct);
        var text = TextOf(pdf);
        text.ShouldContain(invoice.Number);
        text.ShouldContain("FACTURE");
        text.ShouldContain(file.Number);

        // "Ma réservation" offers the invoice to the customer, for this booking only.
        await using (var documentsScope = host.CreateScope())
        {
            var listed = new List<(ICustomerDocumentProvider Provider, CustomerDocumentInfo Document)>();
            foreach (var provider in documentsScope.ServiceProvider.GetServices<ICustomerDocumentProvider>())
            {
                listed.AddRange((await provider.GetDocumentsAsync(file.BookingId, Ct)).Select(d => (provider, d)));
            }

            var (billing, document) = listed.Single(d => d.Document.Kind == CustomerDocumentKind.Invoice);
            document.Number.ShouldBe(invoice.Number);
            (await billing.OpenAsync(Guid.NewGuid(), document.Id, Ct)).ShouldBeNull();
            await using var opened = (await billing.OpenAsync(file.BookingId, document.Id, Ct)).ShouldNotBeNull();
            opened.FileName.ShouldBe($"{invoice.Number}.pdf");
            using var copy = new MemoryStream();
            await opened.Content.CopyToAsync(copy, Ct);
            copy.ToArray().ShouldBe(pdf);
        }

        // The e-mail of the buyer carries the stored PDF.
        invoice.EmailedTo.ShouldBe(file.DriverEmail);
        var email = await QueuedEmailAsync(invoice);
        email.To.ShouldBe(file.DriverEmail);
        var attachment = email.Attachments.ShouldHaveSingleItem();
        (attachment.Container, attachment.Path, attachment.ContentType).ShouldBe((FileContainers.Invoices, invoice.PdfPath, "application/pdf"));

        // The contracts of the other modules see it.
        var ofBooking = await RunAsync<IBillingQueries, IReadOnlyList<InvoiceSummaryInfo>>(host, Accountant, q => q.GetInvoicesOfBookingAsync(file.BookingId, Ct));
        ofBooking.ShouldHaveSingleItem().Number.ShouldBe(invoice.Number);
        var day = TunisTime.DateOf(invoice.IssuedAtUtc);
        var invoiced = await RunAsync<IInvoicedAmountsSource, InvoicedAmountsInfo>(host, Accountant, s => s.GetInvoicedAsync(file.Agency.AgencyId, day, day, Ct));
        (invoiced.InclVat, invoiced.ExclVat, invoiced.InvoiceCount, invoiced.CreditNoteCount).ShouldBe((invoice.TotalInclVat, invoice.TotalExclVat, 1, 0));
    }

    [Fact]
    public async Task Twenty_invoices_issued_in_parallel_take_gapless_numbers()
    {
        var agency = await AgencyAsync(host, Ct, settings: new Dictionary<string, string> { ["billing.issue_automatically"] = "false" });
        var bookings = await ClosedFilesAsync(host, agency, 20, Ct);

        var issued = await Task.WhenAll(bookings.Select(id => RunAsync<InvoicingService, Result<IssuedDocument>>(host, Accountant, s => s.IssueRentalInvoiceAsync(id, Ct))));

        issued.ShouldAllBe(r => r.IsSuccess);
        issued.Select(r => r.Value.Number).Distinct().Count().ShouldBe(20);
        issued.ShouldAllBe(r => !r.Value.AlreadyIssued);

        // Every number of the year of the agency, from 1 to its counter, is used exactly once: the series is the agency's
        // (ADR-0067). The counter row is locked while the invoices are read, so no issue of the agency is in flight.
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var year = int.Parse(issued[0].Value.Number.AsSpan(4, 4), CultureInfo.InvariantCulture);
        var last = 0L;
        List<long> sequences = [];
        Ok(await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(
            async ct =>
            {
                last = await db.Database
                    .SqlQuery<long>($"SELECT [LastValue] AS [Value] FROM [infra].[DocumentCounters] WITH (UPDLOCK, HOLDLOCK) WHERE [AgencyId] = {agency.AgencyId} AND [DocumentType] = 'Invoice' AND [Year] = {year}")
                    .SingleAsync(ct);
                sequences = await db.Set<Invoice>().AsNoTracking().IgnoreAutoIncludes()
                    .Where(i => i.AgencyId == agency.AgencyId && i.FiscalYear == year)
                    .Select(i => i.Sequence)
                    .ToListAsync(ct);
                return Result.Success();
            },
            cancellationToken: Ct));

        last.ShouldBe(20, "a new agency starts its own series");
        sequences.Order().ShouldBe(Enumerable.Range(1, (int)last).Select(n => (long)n));
    }

    [Fact]
    public async Task Credit_notes_take_back_part_then_the_rest_of_an_invoice_and_never_more()
    {
        var agency = await AgencyAsync(host, Ct, settings: new Dictionary<string, string> { ["billing.issue_automatically"] = "false" });
        var bookingId = (await ClosedFilesAsync(host, agency, 1, Ct)).Single();
        var issued = Ok(await RunAsync<InvoicingService, Result<IssuedDocument>>(host, Accountant, s => s.IssueRentalInvoiceAsync(bookingId, Ct)));
        var invoice = await InvoiceOfAsync(host, bookingId, _ => true, Ct);
        var rental = invoice.Lines.OrderByDescending(l => l.InclVat).First();

        var partial = Ok(await RunAsync<InvoicingService, Result<IssuedDocument>>(
            host,
            Accountant,
            s => s.CreateCreditNoteAsync(issued.Id, new CreditNoteRequest(CreditNoteScope.Partial, "Geste commercial", [new CreditLineRequest(rental.Id, 50m)]), Ct)));
        (await RunAsync<InvoicingService, Result<IssuedDocument>>(
                host,
                Accountant,
                s => s.CreateCreditNoteAsync(issued.Id, new CreditNoteRequest(CreditNoteScope.Partial, "Trop", [new CreditLineRequest(rental.Id, rental.InclVat.Amount)]), Ct)))
            .IsFailure.ShouldBeTrue("50.000 of the line were already credited");
        (await RunAsync<InvoicingService, Result<IssuedDocument>>(host, BillingAgent, s => s.CreateCreditNoteAsync(issued.Id, new CreditNoteRequest(CreditNoteScope.Total, "Erreur"), Ct)))
            .Error.ShouldBe(BillingErrors.Forbidden);
        var total = Ok(await RunAsync<InvoicingService, Result<IssuedDocument>>(host, Accountant, s => s.CreateCreditNoteAsync(issued.Id, new CreditNoteRequest(CreditNoteScope.Total, "Annulation du solde"), Ct)));
        (await RunAsync<InvoicingService, Result<IssuedDocument>>(host, Accountant, s => s.CreateCreditNoteAsync(issued.Id, new CreditNoteRequest(CreditNoteScope.Total, "Encore"), Ct)))
            .Error.ShouldBe(BillingErrors.NothingToCredit);

        CreditNoteNumber().IsMatch(partial.Number).ShouldBeTrue(partial.Number);
        CreditNoteNumber().IsMatch(total.Number).ShouldBeTrue(total.Number);
        var first = await DocumentAsync<CreditNote>(host, partial.Id, n => n.PdfPath is not null, Ct);
        var second = await DocumentAsync<CreditNote>(host, total.Id, n => n.PdfPath is not null, Ct);
        first.TotalInclVat.ShouldBe(Money.Tnd(-50m));
        (first.TotalInclVat + second.TotalInclVat).ShouldBe(invoice.TotalInclVat.Negate());
        (first.InvoiceId, first.InvoiceNumber, first.Scope, first.Reason).ShouldBe((invoice.Id, invoice.Number, CreditNoteScope.Partial, "Geste commercial"));
        TextOf(await ReadAsync(host, FileContainers.Invoices, first.PdfPath!, Ct)).ShouldContain(invoice.Number, customMessage: "the credit note names the invoice it corrects");

        var detail = Ok(await RunAsync<BillingAdministrationQueries, Result<InvoiceDetail>>(host, Accountant, q => q.GetInvoiceAsync(invoice.Id, Ct)));
        detail.Remaining.ShouldBe(Money.Zero);
        detail.CreditNotes.Select(n => n.Number).ShouldBe([partial.Number, total.Number]);
        detail.Actions.CanCredit.ShouldBeFalse();

        var day = TunisTime.DateOf(invoice.IssuedAtUtc);
        var invoiced = await RunAsync<IInvoicedAmountsSource, InvoicedAmountsInfo>(host, Accountant, s => s.GetInvoicedAsync(agency.AgencyId, day, day.AddDays(1), Ct));
        (invoiced.InclVat, invoiced.InvoiceCount, invoiced.CreditNoteCount).ShouldBe((Money.Zero, 1, 2));
        var sales = await RunAsync<IBillingQueries, IReadOnlyList<SalesDocumentInfo>>(host, Accountant, q => q.GetSalesDocumentsAsync(agency.AgencyId, day, day.AddDays(1), Ct));
        sales.Select(s => s.Type).ShouldBe([SalesDocumentTypeInfo.Invoice, SalesDocumentTypeInfo.CreditNote, SalesDocumentTypeInfo.CreditNote], ignoreOrder: true);
        sales.Where(s => s.Type == SalesDocumentTypeInfo.CreditNote).ShouldAllBe(s => s.InvoiceId == invoice.Id);
    }

    [Fact]
    public async Task The_pdf_of_an_invoice_is_served_to_the_readers_of_invoices_with_an_mfa_session_only()
    {
        var agency = await AgencyAsync(host, Ct, settings: new Dictionary<string, string> { ["billing.issue_automatically"] = "false" });
        var bookingId = (await ClosedFilesAsync(host, agency, 1, Ct)).Single();
        var issued = Ok(await RunAsync<InvoicingService, Result<IssuedDocument>>(host, Accountant, s => s.IssueRentalInvoiceAsync(bookingId, Ct)));
        await InvoiceOfAsync(host, bookingId, i => i.PdfPath is not null, Ct);
        var url = new Uri($"/admin/invoices/{issued.Id:D}/pdf", UriKind.Relative);

        using var anonymous = host.CreateClient();
        var visitor = await anonymous.GetAsync(url, Ct);
        visitor.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(visitor.Headers.Location!).ShouldContain("/account/login");

        using var preparer = host.CreateClient();
        var (_, preparerEmail) = await TestStaff.CreateAsync(host, Roles.Preparer, agencyId: agency.AgencyId);
        await HttpSignIn.SignInWithTwoFactorAsync(preparer, preparerEmail, "/admin", Ct);
        var refused = await preparer.GetAsync(url, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(refused.Headers.Location!).ShouldContain("/account/access-denied");

        using var accountant = host.CreateClient();
        var (_, accountantEmail) = await TestStaff.CreateAsync(host, Roles.Accountant, agencyId: agency.AgencyId);
        await HttpSignIn.SignInWithTwoFactorAsync(accountant, accountantEmail, "/admin", Ct);
        var response = await accountant.GetAsync(url, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        // The buyer is personal data: the browser does not keep the invoice either (security review, pass 2).
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.CacheControl.Private.ShouldBeTrue();
        response.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe($"{issued.Number}.pdf");
        TextOf(await response.Content.ReadAsByteArrayAsync(Ct)).ShouldContain(issued.Number);

        // The English courtesy translation is rendered on demand, never kept.
        var english = await accountant.GetAsync(new Uri($"/admin/invoices/{issued.Id:D}/pdf?lang=en", UriKind.Relative), Ct);
        english.StatusCode.ShouldBe(HttpStatusCode.OK);
        english.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var englishText = TextOf(await english.Content.ReadAsByteArrayAsync(Ct));
        englishText.ShouldContain("INVOICE");
        englishText.ShouldContain("Courtesy");
        englishText.ShouldContain(issued.Number);

        (await accountant.GetAsync(new Uri($"/admin/invoices/{Guid.CreateVersion7():D}/pdf", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_agency_on_ttn_transmits_its_invoices_through_the_provider()
    {
        var agency = await AgencyAsync(host, Ct, settings: new Dictionary<string, string>
        {
            ["billing.issue_automatically"] = "false",
            ["billing.einvoice_ttn"] = "true",
        });
        var bookingId = (await ClosedFilesAsync(host, agency, 1, Ct)).Single();

        var issued = Ok(await RunAsync<InvoicingService, Result<IssuedDocument>>(host, Accountant, s => s.IssueRentalInvoiceAsync(bookingId, Ct)));
        var invoice = await InvoiceOfAsync(host, bookingId, i => i.EInvoiceStatus == EInvoiceStatus.Transmitted && i.PdfPath is not null, Ct);

        invoice.Id.ShouldBe(issued.Id);
        invoice.EInvoiceMode.ShouldBe(EInvoiceMode.Ttn);
        invoice.TtnReference.ShouldBe($"TTN-FAKE-{invoice.Number}");
        invoice.TransmittedAtUtc.ShouldNotBeNull();
        invoice.Mentions.ShouldNotContain(m => m.Contains("transitoire", StringComparison.Ordinal));
        TextOf(await ReadAsync(host, FileContainers.Invoices, invoice.PdfPath!, Ct)).ShouldContain(invoice.TtnReference!);
        var queue = Ok(await RunAsync<BillingAdministrationQueries, Result<IReadOnlyList<EInvoiceQueueItem>>>(host, Accountant, q => q.GetEInvoiceQueueAsync(agency.AgencyId, Ct)));
        queue.ShouldNotContain(q => q.Id == invoice.Id);
    }

    [Fact]
    public async Task A_closed_file_of_an_agency_without_seller_identity_waits_in_the_list_to_invoice()
    {
        var agency = await AgencyWithoutIdentityAsync();
        var bookingId = (await ClosedFilesAsync(host, agency, 1, Ct)).Single();

        (await RunAsync<InvoicingService, Result<IssuedDocument>>(host, Accountant, s => s.IssueRentalInvoiceAsync(bookingId, Ct))).Error.ShouldBe(BillingErrors.SellerIdentityIncomplete);
        var waiting = Ok(await RunAsync<BillingAdministrationQueries, Result<IReadOnlyList<FileToInvoice>>>(host, Accountant, q => q.GetFilesToInvoiceAsync(agency.AgencyId, Ct)));
        waiting.ShouldContain(f => f.BookingId == bookingId && f.Kind == InvoiceKind.Rental && f.BlockingErrorCode == BillingErrors.SellerIdentityIncomplete.Code);

        await IdentifyAsync(host, agency.AgencyId, Ct);
        Ok(await RunAsync<InvoicingService, Result<IssuedDocument>>(host, Accountant, s => s.IssueRentalInvoiceAsync(bookingId, Ct)));
        Ok(await RunAsync<BillingAdministrationQueries, Result<IReadOnlyList<FileToInvoice>>>(host, Accountant, q => q.GetFilesToInvoiceAsync(agency.AgencyId, Ct)))
            .ShouldNotContain(f => f.BookingId == bookingId);
    }

    /// <summary>A test agency whose legal identity is not filled in (its files cannot be invoiced yet).</summary>
    private async Task<BillingAgency> AgencyWithoutIdentityAsync()
    {
        var agency = await Bookings.BookingWorld.CreateAsync(host, Ct, ecmrVehicles: 1, cdmrVehicles: 0);
        await SetAsync(host, agency.AgencyId, "billing.issue_automatically", "false", Ct);
        return new BillingAgency(agency, string.Empty);
    }

    /// <summary>
    /// The e-mail queued in the outbox for <paramref name="invoice"/>: its subject names the number and it carries the PDF
    /// of that very invoice (numbers are unique within an agency only, ADR-0067).
    /// </summary>
    private async Task<EmailMessage> QueuedEmailAsync(Invoice invoice)
    {
        await using var scope = host.CreateScope();
        var path = invoice.PdfPath.ShouldNotBeNull();
        var payloads = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<OutboxMessage>()
            .AsNoTracking()
            .Where(m => m.Type == typeof(EmailRequested).FullName && m.Payload.Contains(invoice.Number) && m.Payload.Contains(path))
            .Select(m => m.Payload)
            .ToListAsync(Ct);
        return ((EmailRequested)new OutboxSerializer().Deserialize(payloads.ShouldHaveSingleItem(), typeof(EmailRequested))).Message;
    }

    [GeneratedRegex(@"^FAC-\d{4}-\d{6}$")]
    private static partial Regex InvoiceNumber();

    [GeneratedRegex(@"^AV-\d{4}-\d{6}$")]
    private static partial Regex CreditNoteNumber();
}
