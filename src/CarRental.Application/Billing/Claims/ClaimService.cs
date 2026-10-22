using System.Globalization;
using System.Security.Cryptography;
using CarRental.Application.Billing.Invoicing;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Paging;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Billing.Claims;

/// <summary>
/// Claims (sinistres, ADR-0059, permission <c>claims.manage</c>): opened for a vehicle of the agency (and the file during
/// which the accident happened), with the accident report and photos in the private container <c>inspections</c>
/// (<c>claims/{claim}/{file}</c>, checked by their real type, 10 MB at most), the insurer and its claim number, the
/// franchise of the policy, the costs and reimbursements, and the statuses open, at the insurer, closed.
/// </summary>
public sealed class ClaimService
{
    public const long MaxFileBytes = 10 * 1024 * 1024;

    private readonly BillingStore _store;
    private readonly IFleetQueries _fleet;
    private readonly IBookingsQueries _bookings;
    private readonly IFileStorage _storage;

    internal ClaimService(BillingStore store, IFleetQueries fleet, IBookingsQueries bookings, IFileStorage storage)
    {
        _store = store;
        _fleet = fleet;
        _bookings = bookings;
        _storage = storage;
    }

    /// <summary>Opens a claim for a vehicle of the agency (the file, by its number, must be one of the agency).</summary>
    public async Task<Result<Guid>> OpenAsync(ClaimForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!_store.User.HasPermission(Permissions.ClaimsManage))
        {
            return BillingErrors.Forbidden;
        }

        // A claim of an agency the operation does not work for is refused like a vehicle of another agency (ADR-0067).
        var vehicle = await _store.AgencyAsync(form.AgencyId, cancellationToken) is null ? null : await _fleet.GetVehicleAsync(form.VehicleId, cancellationToken);
        if (vehicle is null || vehicle.AgencyId != form.AgencyId)
        {
            return BillingErrors.ClaimVehicleRequired;
        }

        BookingSummaryInfo? booking = null;
        if (!string.IsNullOrWhiteSpace(form.BookingNumber))
        {
            booking = await _bookings.FindByNumberAsync(form.BookingNumber, cancellationToken);
            if (booking is null || booking.AgencyId != form.AgencyId)
            {
                return BillingErrors.BookingNotFound;
            }
        }

        var claim = Claim.Open(
            form.AgencyId,
            form.VehicleId,
            booking?.Id,
            booking?.Number,
            new ClaimDetails(form.AccidentAt, form.Place, form.Description, form.Insurer, form.InsurerClaimNumber, form.Franchise),
            _store.Actor(),
            _store.Now());
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        _store.Db.Set<Claim>().Add(claim.Value);
        await _store.Db.SaveChangesAsync(cancellationToken);
        return claim.Value.Id;
    }

    /// <summary>The claims matching <paramref name="filter"/>, the most recent accident first.</summary>
    public async Task<Result<PagedResult<ClaimListItem>>> ListAsync(ClaimListFilter filter, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        if (!_store.User.HasPermission(Permissions.ClaimsManage))
        {
            return BillingErrors.Forbidden;
        }

        var query = _store.Db.Set<Claim>().AsNoTracking();
        if (filter.AgencyId is { } agencyId)
        {
            query = query.Where(c => c.AgencyId == agencyId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(c => c.Status == status);
        }

        if (filter.VehicleId is { } vehicleId)
        {
            query = query.Where(c => c.VehicleId == vehicleId);
        }

        var total = await query.CountAsync(cancellationToken);
        var claims = await query
            .OrderByDescending(c => c.AccidentAtUtc)
            .ThenBy(c => c.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);
        var labels = await VehicleLabelsAsync(claims.Select(c => c.VehicleId), cancellationToken);
        return new PagedResult<ClaimListItem>(
            [
                .. claims.Select(c => new ClaimListItem(
                    c.Id,
                    c.AccidentAtUtc,
                    c.VehicleId,
                    labels.GetValueOrDefault(c.VehicleId, string.Empty),
                    c.BookingNumber,
                    c.Insurer,
                    c.InsurerClaimNumber,
                    c.Status,
                    c.TotalCosts,
                    c.TotalReimbursements,
                    c.NetCost)),
            ],
            total,
            page.Page,
            page.PageSize);
    }

    /// <summary>The page of a claim.</summary>
    public async Task<Result<ClaimDetail>> GetAsync(Guid claimId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.ClaimsManage))
        {
            return BillingErrors.Forbidden;
        }

        var claim = await _store.Db.Set<Claim>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == claimId, cancellationToken);
        if (claim is null)
        {
            return BillingErrors.ClaimNotFound;
        }

        var labels = await VehicleLabelsAsync([claim.VehicleId], cancellationToken);
        return new ClaimDetail(
            claim.Id,
            claim.AgencyId,
            claim.VehicleId,
            labels.GetValueOrDefault(claim.VehicleId, string.Empty),
            claim.BookingId,
            claim.BookingNumber,
            claim.AccidentAtUtc,
            claim.Place,
            claim.Description,
            claim.Insurer,
            claim.InsurerClaimNumber,
            claim.Franchise,
            claim.Status,
            claim.OpenedAtUtc,
            claim.OpenedBy,
            claim.ClosedAtUtc,
            [.. claim.Costs.Select(c => new ClaimEntryView(c.Id, c.Date, c.Label, c.Amount, c.Reference, c.RecordedAtUtc, c.RecordedBy))],
            [.. claim.Reimbursements.Select(r => new ClaimEntryView(r.Id, r.Date, r.Label, r.Amount, r.Reference, r.RecordedAtUtc, r.RecordedBy))],
            [.. claim.Documents.Select(d => new ClaimDocumentView(d.Id, d.Kind, d.FileName, d.ContentType, d.Length, d.UploadedAtUtc, d.UploadedBy))],
            claim.TotalCosts,
            claim.TotalReimbursements,
            claim.NetCost);
    }

    public Task<Result> UpdateAsync(Guid claimId, ClaimDetails details, CancellationToken cancellationToken = default) =>
        ChangeAsync(claimId, claim => claim.UpdateDetails(details, _store.Actor(), _store.Now()), cancellationToken);

    public Task<Result> AddCostAsync(Guid claimId, ClaimEntryInput input, CancellationToken cancellationToken = default) =>
        ChangeAsync(claimId, claim => claim.AddCost(input, _store.Actor(), _store.Now()), cancellationToken);

    public Task<Result> RemoveCostAsync(Guid claimId, Guid costId, CancellationToken cancellationToken = default) =>
        ChangeAsync(claimId, claim => claim.RemoveCost(costId, _store.Actor(), _store.Now()), cancellationToken);

    public Task<Result> AddReimbursementAsync(Guid claimId, ClaimEntryInput input, CancellationToken cancellationToken = default) =>
        ChangeAsync(claimId, claim => claim.AddReimbursement(input, _store.Actor(), _store.Now()), cancellationToken);

    public Task<Result> RemoveReimbursementAsync(Guid claimId, Guid reimbursementId, CancellationToken cancellationToken = default) =>
        ChangeAsync(claimId, claim => claim.RemoveReimbursement(reimbursementId, _store.Actor(), _store.Now()), cancellationToken);

    public Task<Result> SendToInsurerAsync(Guid claimId, CancellationToken cancellationToken = default) =>
        ChangeAsync(claimId, claim => claim.SendToInsurer(_store.Actor(), _store.Now()), cancellationToken);

    public Task<Result> ReopenAsync(Guid claimId, CancellationToken cancellationToken = default) =>
        ChangeAsync(claimId, claim => claim.Reopen(_store.Actor(), _store.Now()), cancellationToken);

    public Task<Result> CloseAsync(Guid claimId, CancellationToken cancellationToken = default) =>
        ChangeAsync(claimId, claim => claim.Close(_store.Actor(), _store.Now()), cancellationToken);

    /// <summary>Removes a file of the claim; its blob is deleted after the commit.</summary>
    public Task<Result> RemoveDocumentAsync(Guid claimId, Guid documentId, CancellationToken cancellationToken = default) =>
        ChangeAsync(claimId, claim => claim.RemoveDocument(documentId, _store.Actor(), _store.Now()), cancellationToken);

    /// <summary>
    /// Stores a file of the claim (accident report, photo, other): its real type is read from its first bytes (JPEG, PNG,
    /// WebP, PDF), its size bounded; the name sent by the device is kept for display only.
    /// </summary>
    public async Task<Result<Guid>> UploadAsync(Guid claimId, ClaimDocumentKind kind, string? fileName, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!_store.User.HasPermission(Permissions.ClaimsManage))
        {
            return BillingErrors.Forbidden;
        }

        var bytes = await ClaimFiles.ReadBoundedAsync(content, MaxFileBytes, cancellationToken);
        if (bytes is null || ClaimFiles.TypeOf(bytes) is not { } type)
        {
            return BillingErrors.ClaimFileInvalid;
        }

        if (!await _store.Db.Set<Claim>().AsNoTracking().AnyAsync(c => c.Id == claimId, cancellationToken))
        {
            return BillingErrors.ClaimNotFound;
        }

        var path = string.Create(CultureInfo.InvariantCulture, $"claims/{claimId:N}/{Guid.CreateVersion7():N}.{type.Extension}");
        await using (var stream = new MemoryStream(bytes, writable: false))
        {
            await _storage.UploadAsync(FileContainers.Inspections, path, stream, type.ContentType, cancellationToken);
        }

        Result<Guid> added;
        try
        {
            var claim = await _store.Db.Set<Claim>().FirstOrDefaultAsync(c => c.Id == claimId, cancellationToken);
            var name = ClaimFiles.NameOf(fileName);
            added = claim is null
                ? BillingErrors.ClaimNotFound
                : claim.AddDocument(
                    new ClaimFileInput(kind, string.IsNullOrWhiteSpace(name) ? $"{kind}.{type.Extension}" : name, path, type.ContentType, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes))),
                    _store.Actor(),
                    _store.Now());
            if (added.IsSuccess)
            {
                await _store.Db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            added = BillingErrors.ConcurrentUpdate;
        }
        catch
        {
            await _storage.DeleteAsync(FileContainers.Inspections, path, CancellationToken.None);
            throw;
        }

        if (added.IsFailure)
        {
            await _storage.DeleteAsync(FileContainers.Inspections, path, CancellationToken.None);
        }

        return added;
    }

    /// <summary>A file of a claim, to stream to the back office (permission <c>claims.manage</c>).</summary>
    public async Task<Result<ClaimFile>> FindFileAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.ClaimsManage))
        {
            return BillingErrors.Forbidden;
        }

        var document = await _store.Db.Set<Claim>()
            .AsNoTracking()
            .SelectMany(c => c.Documents)
            .Where(d => d.Id == documentId)
            .Select(d => new { d.StoragePath, d.ContentType, d.ContentHash, d.FileName })
            .FirstOrDefaultAsync(cancellationToken);
        return document is null
            ? BillingErrors.ClaimEntryNotFound
            : new ClaimFile(FileContainers.Inspections, document.StoragePath, document.ContentType, document.ContentHash[..16], document.FileName);
    }

    public Task<FileDownload?> OpenAsync(ClaimFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return _storage.OpenReadAsync(file.Container, file.Path, cancellationToken);
    }

    private async Task<Result> ChangeAsync(Guid claimId, Func<Claim, Result> change, CancellationToken cancellationToken)
    {
        if (!_store.User.HasPermission(Permissions.ClaimsManage))
        {
            return BillingErrors.Forbidden;
        }

        var claim = await _store.Db.Set<Claim>().FirstOrDefaultAsync(c => c.Id == claimId, cancellationToken);
        if (claim is null)
        {
            return BillingErrors.ClaimNotFound;
        }

        var changed = change(claim);
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

    private async Task<Dictionary<Guid, string>> VehicleLabelsAsync(IEnumerable<Guid> vehicleIds, CancellationToken cancellationToken)
    {
        var labels = new Dictionary<Guid, string>();
        foreach (var vehicleId in vehicleIds.Distinct())
        {
            if (await _fleet.GetVehicleAsync(vehicleId, cancellationToken) is { } vehicle)
            {
                labels[vehicleId] = $"{vehicle.PlateNumber} · {vehicle.Make} {vehicle.Model}";
            }
        }

        return labels;
    }
}

/// <summary>The real type of a file of a claim and its bounded read.</summary>
internal static class ClaimFiles
{
    public sealed record FileType(string ContentType, string Extension);

    /// <summary>
    /// The name of a file as a device sent it, without its folders whatever their separator (a Windows path read on Linux
    /// keeps its backslashes with <see cref="Path.GetFileName(string)"/>); kept for display only.
    /// </summary>
    public static string? NameOf(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? null : fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..].Trim();

    /// <summary>The whole content when it has at most <paramref name="maxBytes"/> bytes, else <see langword="null"/>.</summary>
    public static async Task<byte[]?> ReadBoundedAsync(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>JPEG, PNG, WebP or PDF from the first bytes; <see langword="null"/> otherwise.</summary>
    public static FileType? TypeOf(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0xFF, 0xD8, 0xFF, ..] => new FileType("image/jpeg", "jpg"),
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => new FileType("image/png", "png"),
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => new FileType("image/webp", "webp"),
        [0x25, 0x50, 0x44, 0x46, 0x2D, ..] => new FileType("application/pdf", "pdf"),
        _ => null,
    };
}

/// <summary>Deletes the blob of a file removed from a claim, once the removal is committed (idempotent).</summary>
internal sealed class ClaimFileDiscardedHandler(IFileStorage storage) : IOutboxHandler<ClaimFileDiscarded>
{
    public async Task HandleAsync(ClaimFileDiscarded message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await storage.DeleteAsync(FileContainers.Inspections, message.Path, cancellationToken);
    }
}
