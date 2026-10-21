using System.Globalization;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Customers.Contracts;
using CarRental.Domain.Bookings.Handover;

namespace CarRental.Application.Bookings.Handover;

/// <summary>Mapping of the hand-over domain to the views and contracts.</summary>
internal static class HandoverViews
{
    /// <summary>Characters of a document number left visible on the screens and the contract.</summary>
    public const int VisibleDocumentCharacters = 4;

    public static InspectionView ToView(this Inspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var number = 0;
        return new InspectionView(
            inspection.Id,
            inspection.Kind,
            inspection.Status,
            inspection.VehicleId,
            inspection.ReferenceMileage,
            inspection.Mileage,
            inspection.FuelEighths,
            inspection.Accessories,
            inspection.Originals,
            inspection.NeedsCleaning,
            inspection.Notes,
            [
                .. inspection.Photos
                    .OrderBy(p => p.TakenAtUtc)
                    .Select(p => new InspectionPhotoView(p.Id, p.Slot, p.DamageMarkId, p.TakenAtUtc, p.TakenBy, p.ThumbnailPath is not null)),
            ],
            [
                .. inspection.Damages
                    .OrderBy(d => d.RecordedAtUtc)
                    .ThenBy(d => d.Id)
                    .Select(d => new DamageMarkView(d.Id, ++number, d.View, d.X, d.Y, d.Type, d.Severity, d.Note, d.IsPreExisting, d.SourceMarkId, d.RecordedAtUtc, d.RecordedBy)),
            ],
            [.. inspection.Signatures.OrderBy(s => s.Role).Select(s => new SignatureView(s.Id, s.Role, s.SignerName, s.SignedAtUtc))],
            inspection.PaperSignature,
            inspection.Deferral,
            [.. inspection.Charges.Select(ToView)],
            inspection.Franchise,
            inspection.FranchiseReduced,
            inspection.MissingGuidedPhotos,
            inspection.StartedAtUtc,
            inspection.StartedBy,
            inspection.CompletedAtUtc,
            inspection.CompletedBy,
            inspection.HandedOverAtUtc);
    }

    public static ReturnChargeView ToView(this ReturnCharge charge)
    {
        ArgumentNullException.ThrowIfNull(charge);
        return new ReturnChargeView(
            charge.Position,
            charge.Kind,
            charge.LabelArguments,
            charge.Description,
            charge.Reason,
            charge.Quantity,
            charge.UnitPrice,
            charge.ExclVat,
            charge.Vat,
            charge.InclVat,
            charge.VatRate,
            charge.DamageMarkId,
            charge.ProposedInclVat,
            charge.RecordedBy,
            charge.RecordedAtUtc);
    }

    public static ReturnChargeView ToView(this ReturnChargeLine line, int position)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new ReturnChargeView(
            position,
            line.Kind,
            line.LabelArguments,
            line.Description,
            line.Reason,
            line.Quantity,
            line.UnitPrice,
            line.ExclVat,
            line.Vat,
            line.InclVat,
            line.VatRate,
            line.DamageMarkId,
            line.ProposedInclVat,
            null,
            null);
    }

    public static ReturnChargeInfo ToInfo(this ReturnCharge charge)
    {
        ArgumentNullException.ThrowIfNull(charge);
        var kind = (ReturnChargeKindInfo)(int)charge.Kind;
        return new ReturnChargeInfo(
            charge.Position,
            kind,
            ReturnChargeInfo.LabelKeyOf(kind),
            charge.LabelArguments,
            charge.Description,
            charge.Reason,
            charge.Quantity,
            charge.UnitPrice,
            charge.ExclVat,
            charge.Vat,
            charge.InclVat,
            charge.VatRate,
            charge.DamageMarkId,
            charge.ProposedInclVat,
            charge.RecordedAtUtc,
            charge.RecordedBy);
    }

    public static RentalContractInfo ToInfo(this RentalContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return new RentalContractInfo(
            contract.Id,
            contract.BookingId,
            contract.Number,
            contract.Version,
            (ContractLanguageInfo)(int)contract.Language,
            contract.FileName,
            contract.GeneratedAtUtc,
            contract.GeneratedBy,
            contract.SentTo,
            contract.SentAtUtc);
    }

    /// <summary>
    /// A document number masked except its last <see cref="VisibleDocumentCharacters"/> characters (the Customers module
    /// may already have masked it for a user without <c>customers.documents.read</c>).
    /// </summary>
    public static string Mask(string number)
    {
        ArgumentNullException.ThrowIfNull(number);
        var compact = number.Trim();
        if (compact.Length <= VisibleDocumentCharacters)
        {
            return new string('•', Math.Max(1, compact.Length));
        }

        var visible = compact[^VisibleDocumentCharacters..];
        return string.Create(CultureInfo.InvariantCulture, $"{new string('•', compact.Length - VisibleDocumentCharacters)}{visible}");
    }

    public static HandoverDocumentView ToView(this IdentityDocumentInfo document, DateOnly returnDate)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new HandoverDocumentView(
            document.Type,
            Mask(document.Number),
            document.IssuingCountry,
            document.ExpiresOn,
            document.IsVerified,
            document.VerifiedBy,
            document.IsValidUntil(returnDate));
    }
}
