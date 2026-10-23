using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Files;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings.Handover;

/// <summary>
/// Read side of the hand-over screens: the day list (departures, returns and late returns with their readiness,
/// <c>handovers.manage</c>), the hand-over summary of a file page and the files streamed by the back office
/// (<c>bookings.read</c>; a signature needs <c>handovers.manage</c>).
/// </summary>
public sealed class HandoverQueries
{
    private readonly HandoverStore _store;
    private readonly IBookingsQueries _bookings;
    private readonly IFleetQueries _fleet;
    private readonly IReferentialQueries _referential;
    private readonly IHandoverQueries _handovers;
    private readonly HandoverBalances _balances;

    internal HandoverQueries(
        HandoverStore store,
        IBookingsQueries bookings,
        IFleetQueries fleet,
        IReferentialQueries referential,
        IHandoverQueries handovers,
        HandoverBalances balances)
    {
        _store = store;
        _bookings = bookings;
        _fleet = fleet;
        _referential = referential;
        _handovers = handovers;
        _balances = balances;
    }

    /// <summary>
    /// The agency the screen shows: the agency of the session (ADR-0067); <c>booking.agency_required</c> outside any agency.
    /// </summary>
    public async Task<Result<AgencyInfo>> GetCurrentAgencyAsync(CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.HandoversManage))
        {
            return BookingErrors.Forbidden;
        }

        if (_store.User.AgencyId is not { } agencyId)
        {
            return BookingErrors.AgencyRequired;
        }

        var agency = (await _referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Id == agencyId);
        return agency is null ? BookingErrors.AgencyRequired : agency;
    }

    /// <summary>The departures and returns of an agency on a Tunis day, and every late return, with their readiness.</summary>
    public async Task<Result<HandoverDay>> GetDayAsync(Guid agencyId, DateOnly date, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.HandoversManage))
        {
            return BookingErrors.Forbidden;
        }

        var agenda = await _bookings.GetDayAgendaAsync(agencyId, date, cancellationToken);
        var late = await _bookings.GetLateReturnsAsync(agencyId, cancellationToken);
        var departures = new List<HandoverDayItem>();
        foreach (var item in agenda.Departures)
        {
            departures.Add(await ItemAsync(item.BookingId, item.Number, item.At, item.CategoryCode, item.PlateNumber, item.CustomerName, item.Phone, item.Documents, item.IsDone, item.IsLate, InspectionKind.Departure, cancellationToken));
        }

        var returns = new List<HandoverDayItem>();
        foreach (var item in agenda.Returns)
        {
            returns.Add(await ItemAsync(item.BookingId, item.Number, item.At, item.CategoryCode, item.PlateNumber, item.CustomerName, item.Phone, item.Documents, item.IsDone, item.IsLate, InspectionKind.Return, cancellationToken));
        }

        var lateReturns = new List<HandoverDayItem>();
        foreach (var item in late.Where(l => returns.All(r => r.BookingId != l.BookingId)))
        {
            var category = await _fleet.GetCategoryAsync(item.CategoryId, cancellationToken);
            lateReturns.Add(await ItemAsync(item.BookingId, item.Number, item.ReturnAt, category?.AcrissCode ?? string.Empty, item.PlateNumber, item.CustomerName, item.Phone, DocumentsStateInfo.Unknown, false, true, InspectionKind.Return, cancellationToken));
        }

        return new HandoverDay(agencyId, date, departures, returns, lateReturns);
    }

    /// <summary>The hand-overs of a file (its page): inspections, contracts and balance.</summary>
    public async Task<Result<HandoverSummary>> GetSummaryAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.BookingsRead))
        {
            return BookingErrors.Forbidden;
        }

        var departure = await _store.FindForBookingAsync(bookingId, InspectionKind.Departure, tracked: false, cancellationToken);
        var back = await _store.FindForBookingAsync(bookingId, InspectionKind.Return, tracked: false, cancellationToken);
        return new HandoverSummary(
            bookingId,
            departure?.ToView(),
            back?.ToView(),
            await _handovers.GetContractsAsync(bookingId, cancellationToken),
            _store.User.HasPermission(Permissions.PaymentsRead) || _store.User.HasPermission(Permissions.HandoversManage)
                ? await _handovers.GetBalanceAsync(bookingId, cancellationToken)
                : null);
    }

    /// <summary>A photo (or its thumbnail) of an inspection.</summary>
    public async Task<Result<HandoverMediaFile>> FindPhotoAsync(Guid photoId, bool thumbnail, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.BookingsRead))
        {
            return BookingErrors.Forbidden;
        }

        var photo = await _store.Db.Set<Inspection>()
            .AsNoTracking()
            .SelectMany(i => i.Photos)
            .Where(p => p.Id == photoId)
            .Select(p => new { p.StoragePath, p.ContentType, p.ContentHash, p.ThumbnailPath, p.ThumbnailContentType })
            .FirstOrDefaultAsync(cancellationToken);
        if (photo is null)
        {
            return HandoverErrors.PhotoNotFound;
        }

        return thumbnail && photo.ThumbnailPath is { } small
            ? new HandoverMediaFile(FileContainers.Inspections, small, photo.ThumbnailContentType ?? HandoverFile.Jpeg, $"{photo.ContentHash[..16]}-t", $"{photoId:N}-thumb.{HandoverMedia.ExtensionOf(photo.ThumbnailContentType ?? HandoverFile.Jpeg)}")
            : new HandoverMediaFile(FileContainers.Inspections, photo.StoragePath, photo.ContentType, photo.ContentHash[..16], $"{photoId:N}.{HandoverMedia.ExtensionOf(photo.ContentType)}");
    }

    /// <summary>A drawn signature (personal data: hand-over staff only).</summary>
    public async Task<Result<HandoverMediaFile>> FindSignatureAsync(Guid signatureId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.HandoversManage))
        {
            return BookingErrors.Forbidden;
        }

        var signature = await _store.Db.Set<Inspection>()
            .AsNoTracking()
            .SelectMany(i => i.Signatures)
            .Where(s => s.Id == signatureId)
            .Select(s => new { s.StoragePath, s.ContentHash })
            .FirstOrDefaultAsync(cancellationToken);
        return signature is null
            ? HandoverErrors.InspectionNotFound
            : new HandoverMediaFile(FileContainers.Inspections, signature.StoragePath, HandoverFile.Png, signature.ContentHash[..16], $"{signatureId:N}.png");
    }

    /// <summary>A rental contract (PDF).</summary>
    public async Task<Result<HandoverMediaFile>> FindContractAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.BookingsRead))
        {
            return BookingErrors.Forbidden;
        }

        var contract = await _store.Db.Set<RentalContract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken);
        return contract is null
            ? HandoverErrors.ContractNotFound
            : new HandoverMediaFile(FileContainers.Contracts, contract.StoragePath, HandoverFile.Pdf, contract.ContentHash[..16], contract.FileName);
    }

    public Task<FileDownload?> OpenAsync(HandoverMediaFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return _store.Storage.OpenReadAsync(file.Container, file.Path, cancellationToken);
    }

    private async Task<HandoverDayItem> ItemAsync(
        Guid bookingId,
        string number,
        DateTimeOffset at,
        string categoryCode,
        string? plate,
        string customer,
        string? phone,
        DocumentsStateInfo documents,
        bool done,
        bool late,
        InspectionKind kind,
        CancellationToken cancellationToken)
    {
        var booking = await _store.FindBookingAsync(bookingId, cancellationToken);
        var inspection = await _store.FindForBookingAsync(bookingId, kind, tracked: false, cancellationToken);
        var vehicle = booking is null ? null : await HandoverContextLoader.VehicleAsync(booking, _fleet, cancellationToken);
        var charges = inspection is { Kind: InspectionKind.Return, Status: InspectionStatus.Completed } ? inspection.ChargesTotal : Money.Zero;
        var balance = booking is null ? null : await _balances.ForAsync(booking, charges, cancellationToken);
        var deferral = kind == InspectionKind.Departure ? inspection?.Deferral : null;
        var due = balance is { Balance.IsPositive: true } ? balance.Balance : Money.Zero;
        var readiness = new HandoverReadiness(
            documents,
            vehicle is not null,
            vehicle is { IsCompliant: true } or { IsComplianceWaived: true },
            due,
            balance is not null && (!balance.DepositRequired.IsPositive || balance.DepositsHeld >= balance.DepositRequired),
            deferral is not null);
        return new HandoverDayItem(
            bookingId,
            number,
            booking?.Status ?? BookingStatus.Confirmed,
            at,
            categoryCode,
            plate,
            customer,
            phone,
            done,
            late,
            readiness,
            inspection?.Status);
    }
}

/// <summary><see cref="IHandoverQueries"/>: return charges, contracts and balance of a file for the other modules.</summary>
internal sealed class HandoverContractQueries(HandoverStore store, HandoverBalances balances) : IHandoverQueries
{
    public async Task<ReturnChargesInfo> GetReturnChargesAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var inspection = await store.FindForBookingAsync(bookingId, InspectionKind.Return, tracked: false, cancellationToken);
        if (inspection is not { Status: InspectionStatus.Completed })
        {
            return new ReturnChargesInfo(bookingId, [], Money.Zero, Money.Zero, Money.Zero, null, false);
        }

        var lines = inspection.Charges;
        return new ReturnChargesInfo(
            bookingId,
            [.. lines.Select(l => l.ToInfo())],
            lines.Select(l => l.ExclVat).Sum(),
            lines.Select(l => l.Vat).Sum(),
            lines.Select(l => l.InclVat).Sum(),
            inspection.Franchise,
            inspection.FranchiseReduced);
    }

    public async Task<IReadOnlyList<RentalContractInfo>> GetContractsAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var contracts = await store.Db.Set<RentalContract>()
            .AsNoTracking()
            .Where(c => c.BookingId == bookingId)
            .OrderBy(c => c.Version)
            .ToListAsync(cancellationToken);
        return [.. contracts.Select(c => c.ToInfo())];
    }

    public async Task<BookingBalanceInfo?> GetBalanceAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await store.FindBookingAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return null;
        }

        var inspection = await store.FindForBookingAsync(bookingId, InspectionKind.Return, tracked: false, cancellationToken);
        var charges = inspection is { Status: InspectionStatus.Completed } ? inspection.ChargesTotal : Money.Zero;
        return await balances.ForAsync(booking, charges, cancellationToken);
    }
}
