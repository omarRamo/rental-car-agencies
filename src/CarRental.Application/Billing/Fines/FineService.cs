using System.Globalization;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Billing.Documents;
using CarRental.Application.Billing.Invoicing;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Paging;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Billing.Fines;

/// <summary>
/// Traffic fines (ADR-0059, permission <c>fines.manage</c>): a notice is recorded and matched with the rental that had the
/// vehicle at that instant (<see cref="FineMatching"/>); an agent resolves the ambiguous ones; the designation form of the
/// driver is produced for the authority (personal data: <c>customers.documents.read</c> too, every production logged in
/// the document access log and the audit log); the fine may be re-invoiced to the payer (with <c>invoices.issue</c>),
/// marked paid and closed.
/// </summary>
public sealed class FineService
{
    /// <summary>Audit action of the production of a designation form.</summary>
    public const string DesignationAuditAction = "billing.fine_designation_generated";

    /// <summary>Access recorded in the document access log for each document printed on a designation form.</summary>
    public const string DesignationAccess = "designation_form";

    private readonly BillingStore _store;
    private readonly IBookingsBillingQueries _bookings;
    private readonly IFleetQueries _fleet;
    private readonly ICustomersQueries _customers;
    private readonly IFineDesignationRenderer _renderer;
    private readonly IDocumentAccessLog _accessLog;
    private readonly IAuditTrail _audit;
    private readonly InvoicingService _invoicing;

    internal FineService(
        BillingStore store,
        IBookingsBillingQueries bookings,
        IFleetQueries fleet,
        ICustomersQueries customers,
        IFineDesignationRenderer renderer,
        IDocumentAccessLog accessLog,
        IAuditTrail audit,
        InvoicingService invoicing)
    {
        _store = store;
        _bookings = bookings;
        _fleet = fleet;
        _customers = customers;
        _renderer = renderer;
        _accessLog = accessLog;
        _audit = audit;
        _invoicing = invoicing;
    }

    /// <summary>Records a notice and finds the rental that had the vehicle (none or several: listed for an agent).</summary>
    public async Task<Result<Guid>> RecordAsync(FineForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!_store.User.HasPermission(Permissions.FinesManage))
        {
            return BillingErrors.Forbidden;
        }

        if (await _store.AgencyAsync(form.AgencyId, cancellationToken) is null)
        {
            return BillingErrors.FineTicketRequired;
        }

        var vehicle = await FindVehicleAsync(form.AgencyId, form.PlateNumber, cancellationToken);
        IReadOnlyList<VehicleRentalInfo> candidates = vehicle is null
            ? []
            : await _bookings.FindRentalsOfVehicleAtAsync(vehicle.Id, form.OffenceAt, cancellationToken);
        var match = FineMatching.Match(vehicle?.Id, form.OffenceAt, [.. candidates.Select(Candidate)]);
        var fine = Fine.Record(
            form.AgencyId,
            new FineDetails(form.TicketNumber, form.Authority, form.OffenceAt, form.Place, form.PlateNumber, form.Amount, form.Notes),
            match,
            _store.Actor(),
            _store.Now());
        if (fine.IsFailure)
        {
            return fine.Error;
        }

        var ticket = fine.Value.TicketNumber;
        var authority = fine.Value.Authority;
        if (await _store.Db.Set<Fine>().AnyAsync(f => f.AgencyId == form.AgencyId && f.TicketNumber == ticket && f.Authority == authority, cancellationToken))
        {
            return BillingErrors.FineDuplicate;
        }

        _store.Db.Set<Fine>().Add(fine.Value);
        try
        {
            await _store.Db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A twin recorded meanwhile (unique index on agency, authority and number).
            return BillingErrors.FineDuplicate;
        }

        return fine.Value.Id;
    }

    /// <summary>The fines matching <paramref name="filter"/>, the most recent offence first.</summary>
    public async Task<Result<PagedResult<FineListItem>>> ListAsync(FineListFilter filter, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        if (!_store.User.HasPermission(Permissions.FinesManage))
        {
            return BillingErrors.Forbidden;
        }

        var query = _store.Db.Set<Fine>().AsNoTracking();
        if (filter.AgencyId is { } agencyId)
        {
            query = query.Where(f => f.AgencyId == agencyId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(f => f.Status == status);
        }

        if (filter.OnlyToAssign)
        {
            query = query.Where(f => f.BookingId == null && f.Status != FineStatus.Closed);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            var plate = PlateKeys.Of(term);
            query = query.Where(f => f.TicketNumber.Contains(term)
                                     || (plate.Length > 0 && f.PlateKey.Contains(plate))
                                     || (f.BookingNumber != null && f.BookingNumber.Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(f => f.OffenceAtUtc)
            .ThenBy(f => f.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(f => new FineListItem(f.Id, f.TicketNumber, f.Authority, f.OffenceAtUtc, f.PlateNumber, f.Amount, f.Status, f.Match, f.BookingId, f.BookingNumber, f.InvoiceNumber))
            .ToListAsync(cancellationToken);
        return new PagedResult<FineListItem>(items, total, page.Page, page.PageSize);
    }

    /// <summary>The page of a fine, with the rentals that had the vehicle at the instant and their drivers.</summary>
    public async Task<Result<FineDetail>> GetAsync(Guid fineId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.FinesManage))
        {
            return BillingErrors.Forbidden;
        }

        var fine = await _store.Db.Set<Fine>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == fineId, cancellationToken);
        if (fine is null)
        {
            return BillingErrors.FineNotFound;
        }

        var vehicle = fine.VehicleId is { } vehicleId ? await _fleet.GetVehicleAsync(vehicleId, cancellationToken) : null;
        IReadOnlyList<VehicleRentalInfo> rentals = fine.VehicleId is { } id && fine.Status == FineStatus.Recorded
            ? await _bookings.FindRentalsOfVehicleAtAsync(id, fine.OffenceAtUtc, cancellationToken)
            : [];
        var options = new List<FineRentalOption>();
        foreach (var rental in rentals)
        {
            options.Add(new FineRentalOption(rental.BookingId, rental.Number, rental.CarriedFromUtc, rental.CarriedUntilUtc, await DriversAsync(rental.MainDriverId, rental.SecondaryDriverId, cancellationToken)));
        }

        var driverName = fine.DriverPersonId is { } driverId ? (await _customers.GetPersonAsync(driverId, cancellationToken))?.FullName : null;
        var fee = await _store.Settings.GetAsync(BillingSettings.FineManagementFee, fine.AgencyId, cancellationToken);
        var open = fine.Status != FineStatus.Closed;
        return new FineDetail(
            fine.Id,
            fine.AgencyId,
            fine.TicketNumber,
            fine.Authority,
            fine.OffenceAtUtc,
            fine.Place,
            fine.PlateNumber,
            fine.Amount,
            fine.Notes,
            fine.VehicleId,
            vehicle is null ? null : $"{vehicle.PlateNumber} · {vehicle.Make} {vehicle.Model}",
            fine.Match,
            fine.BookingId,
            fine.BookingNumber,
            fine.DriverPersonId,
            driverName,
            options,
            fine.Status,
            fine.RecordedAtUtc,
            fine.RecordedBy,
            fine.DesignatedAtUtc,
            fine.DesignatedBy,
            fine.InvoiceId,
            fine.InvoiceNumber,
            fine.PaidAtUtc,
            fine.ClosedAtUtc,
            fine.CloseNote,
            new FineActions(
                CanAssign: fine.Status == FineStatus.Recorded && options.Count > 0,
                CanProduceDesignation: fine.HasRental && fine.Status != FineStatus.Closed && _store.User.HasPermission(Permissions.IdentityDocumentsRead),
                CanMarkDesignated: fine.HasRental && fine.Status == FineStatus.Recorded,
                CanReinvoice: fine.HasRental && fine.InvoiceId is null && fine.Status is (FineStatus.Recorded or FineStatus.Designated) && _store.User.HasPermission(Permissions.InvoicesIssue),
                CanMarkPaid: fine.Status is FineStatus.Recorded or FineStatus.Designated or FineStatus.Reinvoiced,
                CanClose: open,
                fee));
    }

    /// <summary>Attaches the fine to a file that had the vehicle at the instant of the offence and to one of its drivers.</summary>
    public async Task<Result> AssignAsync(Guid fineId, Guid bookingId, Guid driverPersonId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.FinesManage))
        {
            return BillingErrors.Forbidden;
        }

        return await ChangeAsync(
            fineId,
            async (fine, ct) =>
            {
                if (fine.VehicleId is not { } vehicleId)
                {
                    return BillingErrors.FineRentalNotCovering;
                }

                var rental = (await _bookings.FindRentalsOfVehicleAtAsync(vehicleId, fine.OffenceAtUtc, ct)).FirstOrDefault(r => r.BookingId == bookingId);
                return rental is null
                    ? BillingErrors.FineRentalNotCovering
                    : fine.AssignRental(Candidate(rental), driverPersonId, _store.Actor(), _store.Now());
            },
            cancellationToken);
    }

    /// <summary>
    /// The designation form of the driver (PDF, not stored): it prints the driver's documents in full, so it needs
    /// <c>customers.documents.read</c>; each production is logged (document access log, audit log). Sending it to the
    /// authority is then recorded by <see cref="MarkDesignatedAsync"/>.
    /// </summary>
    public async Task<Result<FineDesignationFile>> ProduceDesignationFormAsync(Guid fineId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.FinesManage) || !_store.User.HasPermission(Permissions.IdentityDocumentsRead))
        {
            return BillingErrors.Forbidden;
        }

        return await _store.UnitOfWork.ExecuteAsync<FineDesignationFile>(
            async ct =>
            {
                var fine = await _store.Db.Set<Fine>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == fineId, ct);
                if (fine is null)
                {
                    return BillingErrors.FineNotFound;
                }

                if (fine is not { BookingId: { } bookingId, DriverPersonId: { } driverId })
                {
                    return BillingErrors.FineNoRental;
                }

                var person = await _customers.GetPersonAsync(driverId, ct);
                if (person is null || person.IsAnonymized)
                {
                    return BillingErrors.FineDriverInvalid;
                }

                var rental = fine.VehicleId is { } vehicleId
                    ? (await _bookings.FindRentalsOfVehicleAtAsync(vehicleId, fine.OffenceAtUtc, ct)).FirstOrDefault(r => r.BookingId == bookingId)
                    : null;
                var facts = rental is null ? await _bookings.GetBillingFactsAsync(bookingId, ct) : null;
                var vehicle = fine.VehicleId is { } id ? await _fleet.GetVehicleAsync(id, ct) : null;
                var agency = await _store.AgencyAsync(fine.AgencyId, ct);
                var documents = person.Documents
                    .Where(d => d.Type is IdentityDocumentTypeInfo.DrivingLicense or IdentityDocumentTypeInfo.InternationalDrivingPermit
                                or IdentityDocumentTypeInfo.NationalId or IdentityDocumentTypeInfo.Passport)
                    .OrderBy(d => d.Type)
                    .ToList();
                var now = _store.Now();
                var pdf = _renderer.Render(new FineDesignationDocument(
                    agency?.Legal?.LegalName ?? agency?.Name ?? string.Empty,
                    agency?.Legal?.TaxIdentifier,
                    agency?.Legal?.LegalAddress ?? agency?.Address,
                    agency?.Phone,
                    agency?.Email,
                    fine.TicketNumber,
                    fine.Authority,
                    fine.OffenceAtUtc,
                    fine.Place,
                    fine.Amount,
                    fine.PlateNumber,
                    vehicle?.Make,
                    vehicle?.Model,
                    fine.BookingNumber ?? facts?.Number ?? string.Empty,
                    rental?.CarriedFromUtc ?? facts?.ActualPickupAt ?? fine.OffenceAtUtc,
                    rental is null ? facts?.ActualReturnAt : rental.CarriedUntilUtc,
                    person.FullName,
                    person.BirthDate,
                    person.Nationality,
                    person.Address,
                    [.. documents.Select(d => new FineDesignationIdentityDocument(d.Type, d.Number, d.IssuingCountry, d.IssuedOn, d.ExpiresOn))],
                    now,
                    _store.Actor().Name));

                // Personal data printed in full: one line per document in the access log, one event in the audit log.
                var reason = string.Create(CultureInfo.InvariantCulture, $"fine:{fine.TicketNumber}");
                foreach (var document in documents)
                {
                    await _accessLog.RecordAsync(document.Id, DocumentKindOf(document.Type), DesignationAccess, reason, ct);
                }

                await _audit.RecordAsync(
                    new AuditEvent(DesignationAuditAction)
                    {
                        EntityType = nameof(Fine),
                        EntityId = fine.Id.ToString("D"),
                        Details = string.Create(CultureInfo.InvariantCulture, $"booking {fine.BookingNumber}; person {driverId:D}; documents {documents.Count}"),
                    },
                    ct);
                return new FineDesignationFile(pdf, $"designation-{SafeName(fine.TicketNumber)}.pdf");
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>The designation was sent to the authority: the fine is designated (once).</summary>
    public Task<Result> MarkDesignatedAsync(Guid fineId, CancellationToken cancellationToken = default) =>
        _store.User.HasPermission(Permissions.FinesManage)
            ? ChangeAsync(fineId, (fine, _) => Task.FromResult(fine.Designate(_store.Actor(), _store.Now())), cancellationToken)
            : Task.FromResult<Result>(BillingErrors.Forbidden);

    /// <summary>Re-invoices the fine to the payer of its file (with the management fee).</summary>
    public Task<Result<IssuedDocument>> ReinvoiceAsync(Guid fineId, CancellationToken cancellationToken = default) =>
        _invoicing.ReinvoiceFineAsync(fineId, cancellationToken);

    /// <summary>The fine was paid to the authority.</summary>
    public Task<Result> MarkPaidAsync(Guid fineId, CancellationToken cancellationToken = default) =>
        _store.User.HasPermission(Permissions.FinesManage)
            ? ChangeAsync(fineId, (fine, _) => Task.FromResult(fine.MarkPaid(_store.Actor(), _store.Now())), cancellationToken)
            : Task.FromResult<Result>(BillingErrors.Forbidden);

    /// <summary>Nothing left to do, with an optional note.</summary>
    public Task<Result> CloseAsync(Guid fineId, string? note, CancellationToken cancellationToken = default) =>
        _store.User.HasPermission(Permissions.FinesManage)
            ? ChangeAsync(fineId, (fine, _) => Task.FromResult(fine.Close(note, _store.Actor(), _store.Now())), cancellationToken)
            : Task.FromResult<Result>(BillingErrors.Forbidden);

    /// <summary>The vehicles of an agency whose plate has the key of <paramref name="plateNumber"/>.</summary>
    private async Task<VehicleInfo?> FindVehicleAsync(Guid agencyId, string? plateNumber, CancellationToken cancellationToken)
    {
        var key = PlateKeys.Of(plateNumber);
        if (key.Length == 0)
        {
            return null;
        }

        foreach (var category in await _fleet.GetCategoriesAsync(agencyId, includeInactive: true, cancellationToken))
        {
            var vehicle = (await _fleet.GetVehiclesAsync(category.Id, cancellationToken)).FirstOrDefault(v => PlateKeys.Of(v.PlateNumber) == key);
            if (vehicle is not null)
            {
                return vehicle;
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<FineDriverOption>> DriversAsync(Guid? mainDriverId, Guid? secondaryDriverId, CancellationToken cancellationToken)
    {
        var drivers = new List<FineDriverOption>();
        foreach (var (personId, main) in new[] { (mainDriverId, true), (secondaryDriverId, false) })
        {
            if (personId is { } id && await _customers.GetPersonAsync(id, cancellationToken) is { } person)
            {
                drivers.Add(new FineDriverOption(id, person.IsAnonymized ? string.Empty : person.FullName, main));
            }
        }

        return drivers;
    }

    private async Task<Result> ChangeAsync(Guid fineId, Func<Fine, CancellationToken, Task<Result>> change, CancellationToken cancellationToken)
    {
        var fine = await _store.Db.Set<Fine>().FirstOrDefaultAsync(f => f.Id == fineId, cancellationToken);
        if (fine is null)
        {
            return BillingErrors.FineNotFound;
        }

        var changed = await change(fine, cancellationToken);
        if (changed.IsFailure)
        {
            return changed;
        }

        try
        {
            await _store.Db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return BillingErrors.ConcurrentUpdate;
        }

        return Result.Success();
    }

    private static RentalCandidate Candidate(VehicleRentalInfo rental) =>
        new(rental.BookingId, rental.Number, rental.CarriedFromUtc, rental.CarriedUntilUtc, rental.MainDriverId, rental.SecondaryDriverId);

    private static string DocumentKindOf(IdentityDocumentTypeInfo type) => type switch
    {
        IdentityDocumentTypeInfo.NationalId => "identity_card",
        IdentityDocumentTypeInfo.Passport => "passport",
        IdentityDocumentTypeInfo.DrivingLicense => "driving_licence",
        _ => "international_driving_permit",
    };

    private static string SafeName(string value) => string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
}
