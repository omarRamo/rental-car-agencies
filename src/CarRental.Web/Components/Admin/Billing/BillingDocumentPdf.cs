using System.Globalization;
using CarRental.Application.Billing.Documents;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using CarRental.Web.Components.Admin.Handovers;
using CarRental.Web.Resources.Admin.Billing;
using Microsoft.Extensions.Localization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CarRental.Web.Components.Admin.Billing;

/// <summary>
/// An invoice or a credit note as a PDF (QuestPDF, Community licence, ADR-0058): seller with its legal identity, buyer,
/// number and date, the file and the invoiced period (or the corrected invoice and the reason), the lines with their unit
/// price and amount excluding VAT, the VAT per rate, the totals with the stamp duty, the payment situation at the issue,
/// the TTN reference when there is one, the mentions and the footer of the agency. French for the original; English for
/// a courtesy translation, marked as such.
/// </summary>
internal sealed class BillingDocumentPdf(IStringLocalizer<BillingResources> localizer) : IBillingDocumentRenderer
{
    private static readonly Color Ink = Color.FromHex("#1b1f24");
    private static readonly Color Muted = Color.FromHex("#4a5361");
    private static readonly Color Rule = Color.FromHex("#cbd5e1");
    private static readonly Color Danger = Color.FromHex("#b91c1c");

    private static readonly string[] LineColumns = ["Pdf.Column.Quantity", "Pdf.Column.UnitPrice", "Pdf.Column.ExclVat", "Pdf.Column.Rate", "Pdf.Column.Vat", "Pdf.Column.InclVat"];

    private static readonly string[] VatColumns = ["Pdf.Column.Rate", "Pdf.Column.Base", "Pdf.Column.Vat", "Pdf.Column.InclVat"];

    public byte[] Render(BillingPdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        QuestPdfLicense.Configure();
        byte[]? pdf = null;
        BillingTexts.InCulture(document.Language, culture =>
        {
            pdf = Document
                .Create(container => container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(28);
                    page.DefaultTextStyle(style => style.FontSize(9).FontColor(Ink));
                    page.Header().Element(header => Header(header, document, culture));
                    page.Content().PaddingVertical(10).Column(column =>
                    {
                        column.Spacing(10);
                        column.Item().Element(c => Parties(c, document));
                        column.Item().Element(c => References(c, document, culture));
                        column.Item().Element(c => Lines(c, document, culture));
                        column.Item().Element(c => Totals(c, document, culture));
                        if (document.Payments is { } payments)
                        {
                            column.Item().Element(c => Payments(c, payments, culture));
                        }

                        column.Item().Element(c => Electronic(c, document, culture));
                        if (document.Mentions.Count > 0)
                        {
                            column.Item().Element(c => Mentions(c, document));
                        }
                    });
                    page.Footer().Column(footer =>
                    {
                        if (!string.IsNullOrWhiteSpace(document.Seller.Footer))
                        {
                            footer.Item().AlignCenter().Text(document.Seller.Footer).FontSize(7.5f).FontColor(Muted);
                        }

                        footer.Item().AlignCenter().Text(text =>
                        {
                            text.DefaultTextStyle(style => style.FontSize(7.5f).FontColor(Muted));
                            text.Span(T("Pdf.Footer", document.Seller.LegalName, document.Number));
                            text.Span(" — ");
                            text.CurrentPageNumber();
                            text.Span(" / ");
                            text.TotalPages();
                        });
                    });
                }))
                .WithMetadata(new DocumentMetadata
                {
                    Title = $"{T(TitleKey(document))} {document.Number}",
                    Author = document.Seller.LegalName,
                    Subject = document.Number,
                    Keywords = document.Number,
                    Language = culture.Name,
                })
                .GeneratePdf();
            return string.Empty;
        });
        return pdf!;
    }

    private static string TitleKey(BillingPdfDocument document) => document.Type == BillingDocumentType.Invoice ? "Pdf.Title.Invoice" : "Pdf.Title.CreditNote";

    private string T(string key, params object[] arguments) =>
        arguments.Length == 0 ? localizer[key].Value : localizer[key, arguments].Value;

    private static void Section(IContainer container, string title, Action<IContainer> content) =>
        container.Column(column =>
        {
            column.Item().BorderBottom(1).BorderColor(Rule).PaddingBottom(2).Text(title).FontSize(10.5f).SemiBold();
            column.Item().PaddingTop(4).Element(content);
        });

    private void Header(IContainer container, BillingPdfDocument document, CultureInfo culture) =>
        container.Row(row =>
        {
            var seller = document.Seller;
            row.RelativeItem().Column(column =>
            {
                column.Item().Text(seller.LegalName).FontSize(14).Bold();
                column.Item().Text(seller.Address).FontColor(Muted);
                column.Item().Text(T("Pdf.Seller.TaxIdentifier", seller.TaxIdentifier));
                if (seller.TradeRegisterNumber is { } register)
                {
                    column.Item().Text(T("Pdf.Seller.TradeRegister", register));
                }

                var contact = string.Join(" · ", new[] { seller.Phone, seller.Email }.Where(p => !string.IsNullOrWhiteSpace(p)));
                if (contact.Length > 0)
                {
                    column.Item().Text(contact).FontColor(Muted);
                }
            });
            row.ConstantItem(210).AlignRight().Column(column =>
            {
                column.Item().AlignRight().Text(T(TitleKey(document))).FontSize(18).Bold();
                column.Item().AlignRight().Text(T("Pdf.Number", document.Number)).FontSize(11).SemiBold();
                column.Item().AlignRight().Text(T("Pdf.IssueDate", BillingFormat.Date(document.IssueDate, culture)));
                if (document.IsCourtesyTranslation)
                {
                    column.Item().PaddingTop(4).AlignRight().Text(T("Pdf.Courtesy")).FontColor(Danger).Bold();
                }
            });
        });

    private void Parties(IContainer container, BillingPdfDocument document) =>
        Section(container, T("Pdf.Buyer"), body => body.Column(column =>
        {
            var buyer = document.Buyer;
            column.Item().Text(buyer.Name).SemiBold();
            if (buyer.AddressText is { } address)
            {
                column.Item().Text(address);
            }

            if (buyer.TaxIdentifier is { } tax)
            {
                column.Item().Text(T("Pdf.Buyer.TaxIdentifier", tax));
            }
        }));

    private void References(IContainer container, BillingPdfDocument document, CultureInfo culture) =>
        Section(container, T("Pdf.References"), body => body.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(1);
                columns.RelativeColumn(2);
            });
            if (document.Type == BillingDocumentType.CreditNote && document.InvoiceNumber is { } invoiceNumber)
            {
                Fact(table, T("Pdf.CorrectedInvoice"), document.InvoiceIssueDate is { } date
                    ? T("Pdf.CorrectedInvoice.Value", invoiceNumber, BillingFormat.Date(date, culture))
                    : invoiceNumber);
                Fact(table, T("Pdf.Reason"), document.CreditReason ?? string.Empty);
            }

            if (document.InvoiceKind is { } kind)
            {
                Fact(table, T("Pdf.Kind"), T($"Kind.{kind}"));
            }

            if (document.BookingNumber is { } booking)
            {
                Fact(table, T("Pdf.Booking"), booking);
            }

            if (document is { ServiceFromUtc: { } from, ServiceToUtc: { } to })
            {
                Fact(table, T("Pdf.Period"), T("Pdf.Period.Value", BillingFormat.Instant(from, culture), BillingFormat.Instant(to, culture)));
            }

            if (document.ChannelReference is { } reference)
            {
                Fact(table, T("Pdf.ChannelReference"), reference);
            }
        }));

    private static void Fact(TableDescriptor table, string label, string value)
    {
        table.Cell().PaddingVertical(1).Text(label).FontColor(Muted);
        table.Cell().PaddingVertical(1).Text(value);
    }

    private void Lines(IContainer container, BillingPdfDocument document, CultureInfo culture) =>
        Section(container, T("Pdf.Lines"), body => body.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(6);
                columns.ConstantColumn(30);
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
                columns.ConstantColumn(36);
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
            });
            table.Header(header =>
            {
                header.Cell().BorderBottom(0.5f).BorderColor(Rule).Text(T("Pdf.Column.Label")).SemiBold();
                foreach (var title in LineColumns)
                {
                    header.Cell().BorderBottom(0.5f).BorderColor(Rule).AlignRight().Text(T(title)).SemiBold();
                }
            });
            foreach (var line in document.Lines)
            {
                table.Cell().PaddingVertical(1).Text(line.Label);
                table.Cell().PaddingVertical(1).AlignRight().Text(line.Quantity.ToString(culture));
                table.Cell().PaddingVertical(1).AlignRight().Text(BillingFormat.Amount(line.UnitPriceExclVat, culture));
                table.Cell().PaddingVertical(1).AlignRight().Text(BillingFormat.Amount(line.ExclVat, culture));
                table.Cell().PaddingVertical(1).AlignRight().Text(BillingFormat.Rate(line.VatRate, culture));
                table.Cell().PaddingVertical(1).AlignRight().Text(BillingFormat.Amount(line.Vat, culture));
                table.Cell().PaddingVertical(1).AlignRight().Text(BillingFormat.Amount(line.InclVat, culture));
            }
        }));

    private void Totals(IContainer container, BillingPdfDocument document, CultureInfo culture) =>
        container.Row(row =>
        {
            row.RelativeItem().Column(column =>
            {
                column.Item().Text(T("Pdf.VatTotals")).SemiBold();
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                    });
                    table.Header(header =>
                    {
                        foreach (var title in VatColumns)
                        {
                            header.Cell().BorderBottom(0.5f).BorderColor(Rule).AlignRight().Text(T(title)).SemiBold();
                        }
                    });
                    foreach (var total in document.VatTotals)
                    {
                        table.Cell().AlignRight().Text(BillingFormat.Rate(total.Rate, culture));
                        table.Cell().AlignRight().Text(BillingFormat.Amount(total.ExclVat, culture));
                        table.Cell().AlignRight().Text(BillingFormat.Amount(total.Vat, culture));
                        table.Cell().AlignRight().Text(BillingFormat.Amount(total.InclVat, culture));
                    }
                });
            });
            row.ConstantItem(20);
            row.ConstantItem(220).Column(column =>
            {
                Total(column, T("Pdf.TotalExclVat"), document.TotalExclVat, culture);
                Total(column, T("Pdf.TotalVat"), document.TotalVat, culture);
                Total(column, T("Pdf.TotalInclVat"), document.TotalInclVat, culture);
                if (document.Type == BillingDocumentType.Invoice)
                {
                    Total(column, T("Pdf.StampDuty"), document.StampDuty, culture);
                }

                column.Item().BorderTop(1).BorderColor(Ink).PaddingTop(2).Row(line =>
                {
                    line.RelativeItem().Text(T(document.Type == BillingDocumentType.Invoice ? "Pdf.TotalDue" : "Pdf.TotalCredited")).Bold();
                    line.ConstantItem(90).AlignRight().Text(BillingFormat.Amount(document.TotalDue, culture)).Bold();
                });
            });
        });

    private static void Total(ColumnDescriptor column, string label, Money amount, CultureInfo culture) =>
        column.Item().Row(line =>
        {
            line.RelativeItem().Text(label);
            line.ConstantItem(90).AlignRight().Text(BillingFormat.Amount(amount, culture));
        });

    private void Payments(IContainer container, BillingPdfPayments payments, CultureInfo culture) =>
        Section(container, T("Pdf.Payments"), body => body.Column(column =>
        {
            Total(column, T("Pdf.Paid"), payments.Paid, culture);
            if (!payments.DepositApplied.IsZero)
            {
                Total(column, T("Pdf.DepositApplied"), payments.DepositApplied, culture);
            }

            Total(column, T(payments.Balance.IsNegative ? "Pdf.Overpaid" : "Pdf.Balance"), payments.Balance.Abs(), culture);
        }));

    private void Electronic(IContainer container, BillingPdfDocument document, CultureInfo culture) =>
        container.Text(text =>
        {
            if (document.TtnReference is { } reference)
            {
                text.Span(T("Pdf.TtnReference", reference)).SemiBold();
                if (document.TransmittedAtUtc is { } at)
                {
                    text.Span($" — {T("Pdf.TransmittedAt", BillingFormat.Instant(at, culture))}").FontColor(Muted);
                }
            }
            else
            {
                text.Span(T(document.EInvoiceMode == EInvoiceMode.PaperTransitional ? "Pdf.PaperRegime" : "Pdf.TtnPending")).FontColor(Muted);
            }
        });

    private void Mentions(IContainer container, BillingPdfDocument document) =>
        Section(container, T("Pdf.Mentions"), body => body.Column(column =>
        {
            column.Spacing(2);
            foreach (var mention in document.Mentions)
            {
                column.Item().Text(mention).FontSize(8).Justify();
            }

            column.Item().PaddingTop(4).Text(T("Pdf.IssuedBy", BillingFormat.Instant(document.IssuedAtUtc), document.IssuedBy)).FontSize(7.5f).FontColor(Muted);
        }));
}
