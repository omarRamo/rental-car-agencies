using CarRental.Application.Billing.Contracts;
using CarRental.Application.Billing.Invoicing;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Billing.Fakes;
using CarRental.Domain.Billing;

namespace CarRental.Application.Tests.Billing;

/// <summary>
/// What follows an issue (ADR-0058): on paper, the PDF with the transitional mention, stored once and e-mailed once; with
/// TTN, the transmission first — a failure recorded and retried through the outbox — then the PDF with the TTN reference.
/// </summary>
public sealed class BillingDocumentPipelineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OutboxMessageContext Context = new(Guid.CreateVersion7(), BillingHarness.Now, 1, "corr-outbox");

    [Fact]
    public async Task On_paper_the_pdf_is_stored_with_the_transitional_mention_then_emailed_once_with_the_pdf_attached()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        var issued = (await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct)).Value;
        var message = harness.Db.Events.OfType<InvoiceIssued>().ShouldHaveSingleItem();

        await harness.DocumentHandler.HandleAsync(message, Context, Ct);
        await harness.DocumentHandler.HandleAsync(message, Context, Ct);

        var invoice = harness.Db.Rows<Invoice>().ShouldHaveSingleItem();
        invoice.EInvoiceStatus.ShouldBe(EInvoiceStatus.NotRequired);
        invoice.PdfPath.ShouldBe($"2026/FAC-2026-000001-{invoice.Id:N}.pdf");
        invoice.PdfHash.ShouldNotBeNull().Length.ShouldBe(64);
        harness.Storage.Files.ShouldContainKey((FileContainers.Invoices, invoice.PdfPath!));
        var rendered = harness.Renderer.Rendered.ShouldHaveSingleItem("rendered once, never replaced");
        rendered.Mentions.ShouldContain(m => m.Contains("transitoire", StringComparison.Ordinal));
        (rendered.Language, rendered.IsCourtesyTranslation, rendered.Number).ShouldBe(("fr", false, issued.Number));
        rendered.Lines.Select(l => l.Label).ShouldBe(invoice.Lines.Select(l => l.Description), "the original prints the labels as issued");

        var email = harness.Emails.Sent.ShouldHaveSingleItem("e-mailed once despite the replay");
        email.To.ShouldBe("sami@tests.local");
        var attachment = email.Attachments.ShouldHaveSingleItem();
        (attachment.Container, attachment.Path, attachment.FileName, attachment.ContentType)
            .ShouldBe((FileContainers.Invoices, invoice.PdfPath!, "FAC-2026-000001.pdf", "application/pdf"));
        (invoice.EmailedTo, invoice.EmailedAtUtc).ShouldBe(("sami@tests.local", (DateTimeOffset?)BillingHarness.Now));
    }

    [Fact]
    public async Task Each_step_runs_as_the_system_working_for_the_agency_of_the_document()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct);
        var invoice = harness.Db.Rows<Invoice>().ShouldHaveSingleItem();

        await harness.DocumentHandler.HandleAsync(harness.Db.Events.OfType<InvoiceIssued>().Single(), Context, Ct);
        await harness.DocumentHandler.HandleAsync(new BillingDocumentRetryRequested(BillingDocumentType.Invoice, invoice.Id, invoice.AgencyId), Context, Ct);
        await harness.DocumentHandler.HandleAsync(new BillingDocumentRetryRequested(BillingDocumentType.Invoice, invoice.Id), Context, Ct);

        harness.StepActors.Count.ShouldBe(3);
        harness.StepActors.ShouldAllBe(a => a.IsSystem && a.UserName == BillingScopes.ActorName && a.CorrelationId == Context.CorrelationId);
        harness.StepActors.Take(2).ShouldAllBe(a => a.AgencyId == BillingHarness.AgencyId && !a.IsAcrossAgencies, "the agency of the document only");
        harness.StepActors[2].IsAcrossAgencies.ShouldBeTrue("a request queued before the agencies were separated names none");
    }

    [Fact]
    public async Task A_replay_after_the_upload_keeps_the_stored_pdf_and_never_writes_it_again()
    {
        // The invoices container is immutable in production (ADR-0061): a PDF uploaded before a crash is kept as is.
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct);
        var message = harness.Db.Events.OfType<InvoiceIssued>().ShouldHaveSingleItem();
        var issued = harness.Db.Rows<Invoice>().ShouldHaveSingleItem();
        var path = BillingDocument.PdfPathOf(issued.FiscalYear, issued.Number, issued.Id);
        byte[] stored = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37];
        harness.Storage.Files[(FileContainers.Invoices, path)] = stored;

        await harness.DocumentHandler.HandleAsync(message, Context, Ct);

        harness.Renderer.Rendered.ShouldBeEmpty("the stored PDF is not rendered again");
        harness.Storage.Files[(FileContainers.Invoices, path)].ShouldBe(stored);
        var invoice = harness.Db.Rows<Invoice>().ShouldHaveSingleItem();
        invoice.PdfPath.ShouldBe(path);
        invoice.PdfHash.ShouldBe(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stored)));
        harness.Emails.Sent.ShouldHaveSingleItem().Attachments.ShouldHaveSingleItem().Path.ShouldBe(path);
    }

    [Fact]
    public async Task With_ttn_the_document_is_transmitted_first_and_its_pdf_prints_the_reference()
    {
        var harness = new BillingHarness();
        harness.Settings.Values[BillingSettings.TtnEnabled.Key] = true;
        var file = harness.ClosedFile();
        await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct);

        await harness.DocumentHandler.HandleAsync(harness.Db.Events.OfType<InvoiceIssued>().Single(), Context, Ct);

        var invoice = harness.Db.Rows<Invoice>().ShouldHaveSingleItem();
        (invoice.EInvoiceMode, invoice.EInvoiceStatus, invoice.TtnReference).ShouldBe((EInvoiceMode.Ttn, EInvoiceStatus.Transmitted, "TTN-FAC-2026-000001"));
        invoice.Mentions.ShouldNotContain(m => m.Contains("transitoire", StringComparison.Ordinal), "no transitional mention under TTN");
        var sent = harness.Ttn.Calls.ShouldHaveSingleItem();
        (sent.Type, sent.Number, sent.TotalDue, sent.Seller.TaxIdentifier).ShouldBe((BillingDocumentType.Invoice, invoice.Number, invoice.TotalDue, "1234567/A/M/000"));
        sent.Lines.Count.ShouldBe(invoice.Lines.Count);
        harness.Renderer.Rendered.ShouldHaveSingleItem().TtnReference.ShouldBe("TTN-FAC-2026-000001");
        harness.Emails.Sent.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_failed_transmission_is_recorded_retried_by_the_outbox_and_printed_only_once_accepted()
    {
        var harness = new BillingHarness();
        harness.Settings.Values[BillingSettings.TtnEnabled.Key] = true;
        harness.Ttn.FailuresBeforeSuccess = 2;
        var file = harness.ClosedFile();
        await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct);
        var message = harness.Db.Events.OfType<InvoiceIssued>().Single();

        await Should.ThrowAsync<BillingRetryException>(() => harness.DocumentHandler.HandleAsync(message, Context, Ct));
        var invoice = harness.Db.Rows<Invoice>().ShouldHaveSingleItem();
        (invoice.EInvoiceStatus, invoice.TransmissionAttempts).ShouldBe((EInvoiceStatus.Failed, 1));
        invoice.LastTransmissionError.ShouldStartWith(BillingErrors.EInvoiceNotConfigured.Code);
        invoice.PdfPath.ShouldBeNull("the PDF prints the TTN reference: not before it is known");
        harness.Emails.Sent.ShouldBeEmpty();

        // The invoices screen lists the failure and an accountant asks for another attempt.
        var queue = (await harness.As(Roles.Accountant).Administration.GetEInvoiceQueueAsync(BillingHarness.AgencyId, Ct)).Value;
        queue.ShouldHaveSingleItem().Status.ShouldBe(EInvoiceStatus.Failed);
        (await harness.Administration.RetryTransmissionAsync(BillingDocumentType.Invoice, invoice.Id, Ct)).IsSuccess.ShouldBeTrue();
        var retry = harness.Outbox.Messages.OfType<BillingDocumentRetryRequested>().ShouldHaveSingleItem();

        await Should.ThrowAsync<BillingRetryException>(() => harness.DocumentHandler.HandleAsync(retry, Context, Ct));
        await harness.DocumentHandler.HandleAsync(message, Context, Ct);

        (invoice.EInvoiceStatus, invoice.TransmissionAttempts, invoice.TtnReference).ShouldBe((EInvoiceStatus.Transmitted, 3, "TTN-FAC-2026-000001"));
        invoice.PdfPath.ShouldNotBeNull();
        harness.Emails.Sent.ShouldHaveSingleItem();
        (await harness.Administration.RetryTransmissionAsync(BillingDocumentType.Invoice, invoice.Id, Ct)).Error.ShouldBe(BillingErrors.EInvoiceNotFailed);
    }

    [Fact]
    public async Task A_paper_document_is_never_sent_to_ttn()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct);

        await harness.DocumentHandler.HandleAsync(harness.Db.Events.OfType<InvoiceIssued>().Single(), Context, Ct);

        harness.Ttn.Calls.ShouldBeEmpty();
        (await harness.As(Roles.Accountant).Administration.RetryTransmissionAsync(BillingDocumentType.Invoice, harness.Db.Rows<Invoice>().Single().Id, Ct)).Error.ShouldBe(BillingErrors.EInvoiceNotFailed);
    }

    [Fact]
    public async Task Without_an_email_address_the_pdf_is_stored_and_nothing_is_sent()
    {
        var harness = new BillingHarness();
        harness.Customers.Customers[harness.Payer.Id] = harness.Payer with { Email = null };
        var file = harness.ClosedFile();
        await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct);

        await harness.DocumentHandler.HandleAsync(harness.Db.Events.OfType<InvoiceIssued>().Single(), Context, Ct);

        harness.Db.Rows<Invoice>().Single().PdfPath.ShouldNotBeNull();
        harness.Emails.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_courtesy_translation_is_rendered_from_the_label_keys_and_never_stored()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        var issued = (await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct)).Value;

        var pdf = await harness.Pipeline.RenderAsync(BillingDocumentType.Invoice, issued.Id, "en", Ct);

        pdf.ShouldNotBeNull();
        var rendered = harness.Renderer.Rendered.ShouldHaveSingleItem();
        (rendered.Language, rendered.IsCourtesyTranslation).ShouldBe(("en", true));
        rendered.Lines[0].Label.ShouldBe("Quote.Line.Rental.MidSeason()@en");
        harness.Storage.Files.ShouldBeEmpty();
        harness.Db.Rows<Invoice>().Single().PdfPath.ShouldBeNull();
    }

    [Fact]
    public async Task The_stored_pdf_is_served_to_readers_of_invoices_only()
    {
        var harness = new BillingHarness();
        var file = harness.ClosedFile();
        var issued = (await harness.Invoicing.IssueRentalInvoiceAsync(file.BookingId, Ct)).Value;

        (await harness.As(Roles.Accountant).Administration.FindPdfAsync(BillingDocumentType.Invoice, issued.Id, Ct)).Error.ShouldBe(BillingErrors.PdfNotReady);
        await harness.AsSystem().DocumentHandler.HandleAsync(harness.Db.Events.OfType<InvoiceIssued>().Single(), Context, Ct);

        var pdf = (await harness.As(Roles.Accountant).Administration.FindPdfAsync(BillingDocumentType.Invoice, issued.Id, Ct)).Value;
        (pdf.Container, pdf.Path, pdf.FileName, pdf.ContentType).ShouldBe((FileContainers.Invoices, $"2026/FAC-2026-000001-{issued.Id:N}.pdf", "FAC-2026-000001.pdf", "application/pdf"));
        pdf.ETag.Length.ShouldBe(16);
        (await harness.As(Roles.Preparer).Administration.FindPdfAsync(BillingDocumentType.Invoice, issued.Id, Ct)).Error.ShouldBe(BillingErrors.Forbidden);
        (await harness.As(Roles.Accountant).Administration.FindPdfAsync(BillingDocumentType.CreditNote, issued.Id, Ct)).Error.ShouldBe(BillingErrors.CreditNoteNotFound);
    }
}
