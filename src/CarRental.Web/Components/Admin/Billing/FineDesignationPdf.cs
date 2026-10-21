using CarRental.Application.Billing.Documents;
using CarRental.Domain.Common;
using CarRental.Web.Components.Admin.Handovers;
using CarRental.Web.Resources.Admin.Billing;
using Microsoft.Extensions.Localization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CarRental.Web.Components.Admin.Billing;

/// <summary>
/// The designation form of a traffic fine (ADR-0059), in French: the agency (owner of the vehicle), the notice, the
/// vehicle, the rental during which the offence happened and the driver it designates, with the documents the authority
/// needs. TODO(spec): the official form of the authorities is not provided; this is the agency's own letter. The PDF is
/// personal data: produced on demand, never stored, each production logged.
/// </summary>
internal sealed class FineDesignationPdf(IStringLocalizer<BillingResources> localizer) : IFineDesignationRenderer
{
    private static readonly Color Ink = Color.FromHex("#1b1f24");
    private static readonly Color Muted = Color.FromHex("#4a5361");
    private static readonly Color Rule = Color.FromHex("#cbd5e1");

    public byte[] Render(FineDesignationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        QuestPdfLicense.Configure();
        byte[]? pdf = null;
        BillingTexts.InCulture("fr", culture =>
        {
            pdf = Document
                .Create(container => container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(36);
                    page.DefaultTextStyle(style => style.FontSize(10).FontColor(Ink));
                    page.Content().Column(column =>
                    {
                        column.Spacing(12);
                        column.Item().Row(row =>
                        {
                            row.RelativeItem().Column(agency =>
                            {
                                agency.Item().Text(document.AgencyLegalName).FontSize(13).Bold();
                                foreach (var line in new[] { document.AgencyAddress, Tax(document.AgencyTaxIdentifier), Contact(document) }.Where(l => !string.IsNullOrWhiteSpace(l)))
                                {
                                    agency.Item().Text(line!).FontColor(Muted);
                                }
                            });
                            row.ConstantItem(200).AlignRight().Column(recipient =>
                            {
                                recipient.Item().AlignRight().Text(T("Designation.To")).FontColor(Muted);
                                recipient.Item().AlignRight().Text(document.Authority).SemiBold();
                            });
                        });

                        column.Item().Text(T("Designation.Title", document.TicketNumber)).FontSize(15).Bold();
                        column.Item().Text(T(
                            "Designation.Body",
                            document.PlateNumber,
                            string.Join(' ', new[] { document.VehicleMake, document.VehicleModel }.Where(p => !string.IsNullOrWhiteSpace(p))),
                            document.AgencyLegalName,
                            BillingFormat.Instant(document.OffenceAtUtc, culture),
                            document.Place,
                            document.BookingNumber,
                            BillingFormat.Instant(document.RentalFromUtc, culture),
                            document.RentalUntilUtc is { } until ? BillingFormat.Instant(until, culture) : T("Designation.InProgress"))).Justify();

                        column.Item().Element(section => Section(section, T("Designation.Notice"), body => body.Table(table =>
                        {
                            Columns(table);
                            Fact(table, T("Designation.Ticket"), document.TicketNumber);
                            Fact(table, T("Designation.Authority"), document.Authority);
                            Fact(table, T("Designation.OffenceAt"), BillingFormat.Instant(document.OffenceAtUtc, culture));
                            Fact(table, T("Designation.Place"), document.Place);
                            Fact(table, T("Designation.Amount"), BillingFormat.Amount(document.Amount, culture));
                        })));

                        column.Item().Element(section => Section(section, T("Designation.Driver"), body => body.Table(table =>
                        {
                            Columns(table);
                            Fact(table, T("Designation.FullName"), document.DriverFullName);
                            if (document.DriverBirthDate is { } birthDate)
                            {
                                Fact(table, T("Designation.BirthDate"), BillingFormat.Date(birthDate, culture));
                            }

                            if (document.DriverNationality is { } nationality)
                            {
                                Fact(table, T("Designation.Nationality"), nationality);
                            }

                            if (document.DriverAddress is { } address)
                            {
                                var lines = new[] { address.Line1, address.Line2, string.Join(' ', new[] { address.PostalCode, address.City }.Where(p => !string.IsNullOrWhiteSpace(p))), address.Country };
                                Fact(table, T("Designation.Address"), string.Join(", ", lines.Where(p => !string.IsNullOrWhiteSpace(p))));
                            }

                            foreach (var piece in document.DriverDocuments)
                            {
                                var dates = string.Join(" · ", new[]
                                {
                                    piece.IssuedOn is { } issued ? T("Designation.IssuedOn", BillingFormat.Date(issued, culture)) : null,
                                    piece.ExpiresOn is { } expires ? T("Designation.ExpiresOn", BillingFormat.Date(expires, culture)) : null,
                                }.Where(p => p is not null));
                                Fact(table, T($"Designation.Document.{piece.Type}"), string.Join(" — ", new[] { piece.Number, piece.IssuingCountry, dates }.Where(p => !string.IsNullOrWhiteSpace(p))));
                            }
                        })));

                        column.Item().Text(T("Designation.Closing")).Justify();
                        column.Item().PaddingTop(10).Row(row =>
                        {
                            row.RelativeItem().Text(T("Designation.Done", BillingFormat.Date(TunisTime.DateOf(document.GeneratedAtUtc), culture)));
                            row.ConstantItem(200).AlignRight().Column(signature =>
                            {
                                signature.Item().AlignRight().Text(T("Designation.ForTheAgency"));
                                signature.Item().Height(50).Border(0.5f).BorderColor(Rule);
                            });
                        });
                    });
                    page.Footer().AlignCenter().Text(T("Designation.Footer", BillingFormat.Instant(document.GeneratedAtUtc, culture), document.GeneratedBy)).FontSize(7.5f).FontColor(Muted);
                }))
                .WithMetadata(new DocumentMetadata
                {
                    Title = T("Designation.Title", document.TicketNumber),
                    Author = document.AgencyLegalName,
                    Subject = document.TicketNumber,
                    Language = culture.Name,
                })
                .GeneratePdf();
            return string.Empty;
        });
        return pdf!;
    }

    private string T(string key, params object[] arguments) =>
        arguments.Length == 0 ? localizer[key].Value : localizer[key, arguments].Value;

    private string? Tax(string? taxIdentifier) => taxIdentifier is null ? null : T("Pdf.Seller.TaxIdentifier", taxIdentifier);

    private static string? Contact(FineDesignationDocument document)
    {
        var contact = string.Join(" · ", new[] { document.AgencyPhone, document.AgencyEmail }.Where(p => !string.IsNullOrWhiteSpace(p)));
        return contact.Length == 0 ? null : contact;
    }

    private static void Section(IContainer container, string title, Action<IContainer> content) =>
        container.Column(column =>
        {
            column.Item().BorderBottom(1).BorderColor(Rule).PaddingBottom(2).Text(title).FontSize(11).SemiBold();
            column.Item().PaddingTop(4).Element(content);
        });

    private static void Columns(TableDescriptor table) =>
        table.ColumnsDefinition(columns =>
        {
            columns.RelativeColumn(1);
            columns.RelativeColumn(2);
        });

    private static void Fact(TableDescriptor table, string label, string value)
    {
        table.Cell().PaddingVertical(1).Text(label).FontColor(Muted);
        table.Cell().PaddingVertical(1).Text(value);
    }

}
