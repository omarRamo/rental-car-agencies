using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Parties;
using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Common.Files;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Handover;

/// <summary>
/// Builds the rental contract of a departure from the file, its frozen quote, its drivers (documents masked), the agency,
/// the vehicle, the departure inspection and the money situation; the final contract carries the signatures (PNG read
/// from the private container), the preview does not.
/// </summary>
internal sealed class ContractDocumentBuilder(
    HandoverStore store,
    QuoteStore quotes,
    PartyDirectory parties,
    IFleetQueries fleet,
    IReferentialQueries referential)
{
    public async Task<Result<RentalContractDocument>> BuildAsync(
        Booking booking,
        Inspection inspection,
        BookingBalanceInfo balance,
        ContractLanguage language,
        int version,
        DateTimeOffset? actualPickupAt,
        bool preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(balance);
        if (inspection.VehicleId is not { } vehicleId || await fleet.GetVehicleAsync(vehicleId, cancellationToken) is not { } vehicle)
        {
            return HandoverErrors.VehicleRequired;
        }

        if (inspection.Mileage is not { } mileage || inspection.FuelEighths is not { } fuel || inspection.Accessories is not { } accessories)
        {
            return HandoverErrors.ReadingsMissing;
        }

        var quote = await quotes.CurrentAsync(booking, cancellationToken);
        if (quote is null)
        {
            return BookingErrors.QuoteNotFound;
        }

        var agency = (await referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Id == booking.AgencyId);
        var locations = await referential.GetLocationsAsync(booking.AgencyId, cancellationToken);
        var category = await fleet.GetCategoryAsync(booking.CategoryId, cancellationToken);
        var view = quote.ToView();
        var damages = inspection.ToView().Damages;
        var deferral = inspection.Deferral;

        var signatures = preview
            ? new ContractSignatures(null, null, null)
            : new ContractSignatures(
                await SignatureAsync(inspection, SignatureRole.Customer, cancellationToken),
                await SignatureAsync(inspection, SignatureRole.Agent, cancellationToken),
                inspection.PaperSignature?.Reason);

        var amountDue = balance.Balance.IsPositive ? balance.Balance : Money.Zero;
        return new RentalContractDocument(
            booking.Number,
            version,
            language,
            preview,
            new ContractAgency(agency?.Name ?? string.Empty, agency?.City ?? string.Empty, agency?.Address, agency?.Phone, agency?.Email),
            new ContractRental(
                booking.PickupAt,
                booking.ReturnAt,
                actualPickupAt,
                locations.FirstOrDefault(l => l.Id == booking.PickupLocationId)?.Name ?? string.Empty,
                locations.FirstOrDefault(l => l.Id == booking.ReturnLocationId)?.Name ?? string.Empty,
                quote.Days,
                booking.Channel,
                booking.ChannelReference),
            await DriversAsync(booking, cancellationToken),
            new ContractVehicle(vehicle.PlateNumber, vehicle.Make, vehicle.Model, vehicle.Year, category?.AcrissCode ?? string.Empty, category?.Name ?? string.Empty, vehicle.Fuel),
            new ContractInspection(
                mileage,
                fuel,
                accessories,
                [.. damages.Select(d => new ContractDamage(d.Number, d.View, d.X, d.Y, d.Type, d.Severity, d.Note))],
                inspection.Notes),
            new ContractPrice(
                view.Lines,
                view.TotalExclVat,
                view.TotalVat,
                view.TotalInclVat,
                balance.NetPaid,
                amountDue,
                amountDue.IsPositive && deferral is { BalanceDeferred.IsPositive: true }),
            new ContractDeposit(
                booking.Deposit,
                balance.DepositsHeld,
                [],
                quote.Deposit,
                quote.IncludesFranchiseWaiver,
                deferral is { DepositWaived: true }),
            signatures,
            RentalContractTerms.Reference,
            store.Now(),
            store.Actor().Name);
    }

    private async Task<IReadOnlyList<ContractDriver>> DriversAsync(Booking booking, CancellationToken cancellationToken)
    {
        var drivers = new List<ContractDriver>();
        foreach (var (personId, role) in new[] { (booking.MainDriverId, BookingPartyRoleInfo.MainDriver), (booking.SecondaryDriverId, BookingPartyRoleInfo.SecondaryDriver) })
        {
            if (await parties.PersonAsync(personId, cancellationToken) is not { } person)
            {
                continue;
            }

            drivers.Add(new ContractDriver(
                role,
                person.FullName,
                person.BirthDate,
                person.Phone,
                person.Email,
                [.. person.Documents.Select(d => new ContractDocument(d.Type, HandoverViews.Mask(d.Number), d.IssuingCountry, d.ExpiresOn))]));
        }

        return drivers;
    }

    private async Task<ContractSignature?> SignatureAsync(Inspection inspection, SignatureRole role, CancellationToken cancellationToken)
    {
        if (inspection.Signatures.FirstOrDefault(s => s.Role == role) is not { } signature)
        {
            return null;
        }

        var png = await store.ReadAsync(FileContainers.Inspections, signature.StoragePath, cancellationToken);
        return png is null ? null : new ContractSignature(signature.SignerName, signature.SignedAtUtc, png);
    }
}
