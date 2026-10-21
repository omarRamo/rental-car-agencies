using System.Globalization;
using CarRental.Application.Bookings.Handover;
using CarRental.Domain.Bookings.Handover;
using CarRental.Web.Components.Admin.Bookings;
using CarRental.Web.Resources.Admin.Bookings;
using CarRental.Web.Resources.Admin.Handovers;
using CarRental.Web.Resources.Admin.Pricing;
using Microsoft.Extensions.Localization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CarRental.Web.Components.Admin.Handovers;

/// <summary>
/// The rental contract as a PDF (QuestPDF, Community licence, ADR-0041): agency, rental, drivers and their documents
/// (numbers masked), vehicle, readings and accessories, the damage schematics with their numbered marks, the frozen
/// quote and the money situation, the deposit and franchise, the reference of the terms and the signatures. Written in
/// the language of the contract (French or English), with the texts of the back office.
/// </summary>
internal sealed class RentalContractPdf(
    IStringLocalizer<HandoversResources> handovers,
    IStringLocalizer<QuoteResources> quotes,
    IStringLocalizer<BookingsResources> bookings) : IRentalContractRenderer
{
    private const float SchematicScale = 0.75f;

    private static readonly Color Ink = Color.FromHex("#1b1f24");
    private static readonly Color Muted = Color.FromHex("#4a5361");
    private static readonly Color Rule = Color.FromHex("#cbd5e1");
    private static readonly Color Danger = Color.FromHex("#b91c1c");
    private static readonly string[] DamageColumns = ["Contract.Damage.Number", "Contract.Damage.View", "Contract.Damage.Type", "Contract.Damage.Severity", "Contract.Damage.Note"];
    private static readonly string[] AmountColumns = ["Contract.Column.Quantity", "Contract.Column.UnitPrice", "Contract.Column.ExclVat", "Contract.Column.Vat", "Contract.Column.InclVat"];

    public byte[] Render(RentalContractDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        QuestPdfLicense.Configure();
        var culture = HandoverText.CultureOf(document.Language);
        var (previousCulture, previousUiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            return Document
                .Create(container => container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(28);
                    page.DefaultTextStyle(style => style.FontSize(9).FontColor(Ink));
                    page.Header().Element(header => Header(header, document));
                    page.Content().PaddingVertical(10).Column(column =>
                    {
                        column.Spacing(10);
                        column.Item().Element(c => Rental(c, document));
                        column.Item().Element(c => Drivers(c, document));
                        column.Item().Element(c => Vehicle(c, document));
                        column.Item().Element(c => Condition(c, document));
                        column.Item().Element(c => Price(c, document));
                        column.Item().Element(c => Deposit(c, document));
                        column.Item().Element(c => Terms(c, document));
                        column.Item().Element(c => Signatures(c, document));
                    });
                    page.Footer().AlignCenter().Text(text =>
                    {
                        text.DefaultTextStyle(style => style.FontSize(7.5f).FontColor(Muted));
                        text.Span(T("Contract.Footer", document.Agency.Name, document.Number, document.Version));
                        text.Span(" — ");
                        text.CurrentPageNumber();
                        text.Span(" / ");
                        text.TotalPages();
                    });
                }))
                .WithMetadata(new DocumentMetadata
                {
                    Title = $"{T("Contract.Title")} {document.Number}",
                    Author = document.Agency.Name,
                    Subject = document.Number,
                    Keywords = document.Number,
                    Language = culture.Name,
                })
                .GeneratePdf();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private string T(string key, params object[] arguments) =>
        arguments.Length == 0 ? handovers[key].Value : handovers[key, arguments].Value;

    private static void Section(IContainer container, string title, Action<IContainer> content) =>
        container.Column(column =>
        {
            column.Item().BorderBottom(1).BorderColor(Rule).PaddingBottom(2).Text(title).FontSize(11).SemiBold();
            column.Item().PaddingTop(4).Element(content);
        });

    private void Header(IContainer container, RentalContractDocument document) =>
        container.Row(row =>
        {
            row.RelativeItem().Column(column =>
            {
                column.Item().Text(document.Agency.Name).FontSize(14).Bold();
                column.Item().Text(string.Join(" · ", new[] { document.Agency.Address, document.Agency.City }.Where(p => !string.IsNullOrWhiteSpace(p)))).FontColor(Muted);
                column.Item().Text(string.Join(" · ", new[] { document.Agency.Phone, document.Agency.Email }.Where(p => !string.IsNullOrWhiteSpace(p)))).FontColor(Muted);
            });
            row.ConstantItem(220).AlignRight().Column(column =>
            {
                column.Item().AlignRight().Text(T("Contract.Title")).FontSize(16).Bold();
                column.Item().AlignRight().Text(T("Contract.Number", document.Number, document.Version)).FontSize(10).SemiBold();
                if (document.IsPreview)
                {
                    column.Item().AlignRight().Text(T("Contract.Preview")).FontColor(Danger).Bold();
                }
            });
        });

    private void Rental(IContainer container, RentalContractDocument document) =>
        Section(container, T("Contract.Rental"), body => body.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(1);
                columns.RelativeColumn(2);
            });
            var rental = document.Rental;
            Fact(table, T("Contract.Pickup"), $"{BookingFormat.Instant(rental.PickupAt)} · {rental.PickupLocation}");
            if (rental.ActualPickupAt is { } actual)
            {
                Fact(table, T("Contract.ActualPickup"), BookingFormat.Instant(actual));
            }

            Fact(table, T("Contract.Return"), $"{BookingFormat.Instant(rental.ReturnAt)} · {rental.ReturnLocation}");
            Fact(table, T("Contract.Duration"), T("Contract.Days", rental.Days));
            var channel = bookings[$"Channel.{rental.Channel}"].Value;
            Fact(table, T("Contract.Channel"), rental.ChannelReference is { } reference ? $"{channel} · {reference}" : channel);
        }));

    private static void Fact(TableDescriptor table, string label, string value)
    {
        table.Cell().PaddingVertical(1).Text(label).FontColor(Muted);
        table.Cell().PaddingVertical(1).Text(value);
    }

    private void Drivers(IContainer container, RentalContractDocument document) =>
        Section(container, T("Contract.Drivers"), body => body.Column(column =>
        {
            column.Spacing(4);
            foreach (var driver in document.Drivers)
            {
                column.Item().Column(item =>
                {
                    item.Item().Text(text =>
                    {
                        text.Span(driver.FullName).SemiBold();
                        text.Span($" — {T($"Contract.Role.{driver.Role}")}").FontColor(Muted);
                        if (driver.BirthDate is { } birthDate)
                        {
                            text.Span($" — {T("Contract.BirthDate", BookingFormat.Date(birthDate))}").FontColor(Muted);
                        }
                    });
                    var contact = string.Join(" · ", new[] { driver.Phone, driver.Email }.Where(p => !string.IsNullOrWhiteSpace(p)));
                    if (contact.Length > 0)
                    {
                        item.Item().Text(contact).FontColor(Muted);
                    }

                    foreach (var piece in driver.Documents)
                    {
                        var type = T($"Contract.DocumentType.{piece.Type}");
                        item.Item().Text(piece.ExpiresOn is { } expiry
                            ? T("Contract.Document", type, piece.MaskedNumber, piece.IssuingCountry, BookingFormat.Date(expiry))
                            : T("Contract.DocumentNoExpiry", type, piece.MaskedNumber, piece.IssuingCountry));
                    }
                });
            }
        }));

    private void Vehicle(IContainer container, RentalContractDocument document)
    {
        var vehicle = document.Vehicle;
        Section(container, T("Contract.Vehicle"), body => body.Text(T("Contract.VehicleLine", vehicle.PlateNumber, vehicle.Make, vehicle.Model, vehicle.Year, vehicle.CategoryCode, vehicle.CategoryName)));
    }

    private void Condition(IContainer container, RentalContractDocument document)
    {
        var inspection = document.Inspection;
        Section(container, T("Contract.Condition"), body => body.Column(column =>
        {
            column.Spacing(6);
            column.Item().Text(text =>
            {
                text.Span(T("Contract.Mileage", inspection.Mileage.ToString("N0", CultureInfo.CurrentCulture)));
                text.Span("   ");
                text.Span(T("Contract.Fuel", HandoverText.Fuel(inspection.FuelEighths)));
            });
            column.Item().Text(T("Contract.Accessories", Accessories(inspection.Accessories)));
            if (!string.IsNullOrWhiteSpace(inspection.Notes))
            {
                column.Item().Text(T("Contract.Notes", inspection.Notes));
            }

            column.Item().Text(T("Contract.Damages")).SemiBold();
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                });
                foreach (var view in CarSchematic.Views)
                {
                    table.Cell().Padding(3).Element(cell => Schematic(cell, view, inspection.Damages));
                }
            });
            if (inspection.Damages.Count == 0)
            {
                column.Item().Text(T("Contract.NoDamage")).FontColor(Muted);
            }
            else
            {
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(22);
                        columns.RelativeColumn(2);
                        columns.RelativeColumn(2);
                        columns.RelativeColumn(2);
                        columns.RelativeColumn(4);
                    });
                    table.Header(header =>
                    {
                        foreach (var title in DamageColumns)
                        {
                            header.Cell().BorderBottom(0.5f).BorderColor(Rule).Text(T(title)).SemiBold();
                        }
                    });
                    foreach (var damage in inspection.Damages)
                    {
                        table.Cell().Text(damage.Number.ToString(CultureInfo.CurrentCulture));
                        table.Cell().Text(T($"DamageView.{damage.View}"));
                        table.Cell().Text(T($"DamageType.{damage.Type}"));
                        table.Cell().Text(T($"DamageSeverity.{damage.Severity}"));
                        table.Cell().Text(damage.Note ?? string.Empty);
                    }
                });
            }
        }));
    }

    private void Schematic(IContainer container, DamageView view, IReadOnlyList<ContractDamage> damages)
    {
        var (width, height) = CarSchematic.SizeOf(view);
        var drawnWidth = width * SchematicScale;
        var drawnHeight = height * SchematicScale;
        var marks = damages.Where(d => d.View == view).ToList();
        container.Column(column =>
        {
            column.Item().Text(T($"DamageView.{view}")).FontSize(8).FontColor(Muted);
            column.Item().Width(drawnWidth).Height(drawnHeight).Layers(layers =>
            {
                layers.PrimaryLayer().Svg(CarSchematic.Svg(view, marks.Select(m => (m.X, m.Y, false)))).FitArea();
                foreach (var mark in marks)
                {
                    var x = ((float)mark.X / 100f * drawnWidth) - 5f;
                    var y = ((float)mark.Y / 100f * drawnHeight) - 4f;
                    layers.Layer()
                        .OffsetX(x)
                        .OffsetY(y)
                        .Width(10)
                        .Height(8)
                        .AlignCenter()
                        .AlignMiddle()
                        .Text(mark.Number.ToString(CultureInfo.InvariantCulture))
                        .FontSize(5.5f)
                        .Bold();
                }
            });
        });
    }

    private string Accessories(AccessoryChecklist accessories)
    {
        string Present(bool present, AccessoryItem item) => $"{T($"AccessoryItem.{item}")} : {T(present ? "Contract.Present" : "Contract.Absent")}";
        return string.Join(
            " · ",
            Present(accessories.SpareWheel, AccessoryItem.SpareWheel),
            Present(accessories.Jack, AccessoryItem.Jack),
            Present(accessories.WarningTriangle, AccessoryItem.WarningTriangle),
            Present(accessories.SafetyVest, AccessoryItem.SafetyVest),
            $"{T("AccessoryItem.Keys")} : {accessories.Keys.ToString(CultureInfo.CurrentCulture)}",
            $"{T("AccessoryItem.ChildSeats")} : {accessories.ChildSeats.ToString(CultureInfo.CurrentCulture)}",
            Present(accessories.Gps, AccessoryItem.Gps),
            Present(accessories.VehiclePapers, AccessoryItem.VehiclePapers));
    }

    private void Price(IContainer container, RentalContractDocument document)
    {
        var price = document.Price;
        Section(container, T("Contract.Price"), body => body.Column(column =>
        {
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(5);
                    columns.ConstantColumn(30);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(2);
                });
                table.Header(header =>
                {
                    header.Cell().BorderBottom(0.5f).BorderColor(Rule).Text(T("Contract.Column.Label")).SemiBold();
                    foreach (var title in AmountColumns)
                    {
                        header.Cell().BorderBottom(0.5f).BorderColor(Rule).AlignRight().Text(T(title)).SemiBold();
                    }
                });
                foreach (var line in price.Lines)
                {
                    table.Cell().PaddingVertical(1).Text(BookingFormat.LineLabel(quotes, bookings, line));
                    table.Cell().PaddingVertical(1).AlignRight().Text(line.Quantity.ToString(CultureInfo.CurrentCulture));
                    table.Cell().PaddingVertical(1).AlignRight().Text(BookingFormat.Amount(line.UnitPrice));
                    table.Cell().PaddingVertical(1).AlignRight().Text(BookingFormat.Amount(line.ExclVat));
                    table.Cell().PaddingVertical(1).AlignRight().Text(BookingFormat.Amount(line.Vat));
                    table.Cell().PaddingVertical(1).AlignRight().Text(BookingFormat.Amount(line.InclVat));
                }
            });
            column.Item().PaddingTop(4).AlignRight().Column(totals =>
            {
                totals.Item().AlignRight().Text(T("Contract.TotalExclVat", BookingFormat.Amount(price.TotalExclVat)));
                totals.Item().AlignRight().Text(T("Contract.TotalVat", BookingFormat.Amount(price.TotalVat)));
                totals.Item().AlignRight().Text(T("Contract.TotalInclVat", BookingFormat.Amount(price.TotalInclVat))).Bold();
                totals.Item().AlignRight().Text(T("Contract.Paid", BookingFormat.Amount(price.NetPaid)));
                totals.Item().AlignRight().Text(price.BalanceDeferred
                    ? T("Contract.BalanceDeferred", BookingFormat.Amount(price.BalanceDue))
                    : T("Contract.BalanceDue", BookingFormat.Amount(price.BalanceDue)));
            });
        }));
    }

    private void Deposit(IContainer container, RentalContractDocument document)
    {
        var deposit = document.Deposit;
        Section(container, T("Contract.Deposit"), body => body.Column(column =>
        {
            column.Item().Text(T("Contract.DepositRequired", BookingFormat.Amount(deposit.Required)));
            column.Item().Text(deposit.Waived ? T("Contract.DepositWaived") : T("Contract.DepositHeld", BookingFormat.Amount(deposit.Held)));
            column.Item().Text(T("Contract.Franchise", BookingFormat.Amount(deposit.Franchise)));
            if (deposit.FranchiseWaiverBought)
            {
                column.Item().Text(T("Contract.FranchiseWaiver"));
            }
        }));
    }

    private void Terms(IContainer container, RentalContractDocument document) =>
        Section(container, T("Contract.Terms"), body => body.Text(T("Contract.TermsText", document.TermsReference)).Justify());

    private void Signatures(IContainer container, RentalContractDocument document)
    {
        var signatures = document.Signatures;
        Section(container, T("Contract.Signatures"), body => body.Column(column =>
        {
            if (signatures.PaperReason is { } reason)
            {
                column.Item().Text(T("Contract.SignedOnPaper", reason));
            }

            column.Item().Row(row =>
            {
                row.Spacing(20);
                row.RelativeItem().Element(cell => Signature(cell, T("Contract.CustomerSignature"), signatures.Customer));
                row.RelativeItem().Element(cell => Signature(cell, T("Contract.AgentSignature"), signatures.Agent));
            });
            column.Item().PaddingTop(6).Text(T("Contract.GeneratedAt", BookingFormat.Instant(document.GeneratedAtUtc), document.GeneratedBy)).FontSize(7.5f).FontColor(Muted);
        }));
    }

    private void Signature(IContainer container, string title, ContractSignature? signature) =>
        container.Column(column =>
        {
            column.Item().Text(title).SemiBold();
            column.Item().Height(60).Border(0.5f).BorderColor(Rule).Padding(2).Element(box =>
            {
                if (signature is null)
                {
                    box.AlignMiddle().AlignCenter().Text(T("Contract.NotSigned")).FontColor(Muted);
                }
                else
                {
                    box.Image(signature.Png.ToArray()).FitArea();
                }
            });
            if (signature is not null)
            {
                column.Item().Text($"{signature.SignerName} — {T("Contract.SignedAt", BookingFormat.Instant(signature.SignedAtUtc))}").FontSize(8).FontColor(Muted);
            }
        });
}

/// <summary>
/// QuestPDF under its Community licence, set once at startup: free for a company under the revenue threshold of the
/// licence (ADR-0041; to check again if the agency grows past it).
/// </summary>
internal static class QuestPdfLicense
{
    public static void Configure()
    {
        if (global::QuestPDF.Settings.License != LicenseType.Community)
        {
            global::QuestPDF.Settings.License = LicenseType.Community;
        }
    }
}
