using CarRental.Application.Common.Files;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Handover;

/// <summary>
/// The steps shared by the departure and return inspections, on a draft (ADR-0041): readings and accessories, originals
/// seen (departure), cleaning (return), notes, photos (uploaded one by one, a guided view retaken), damages of the
/// schematic, signatures drawn on the tablet or the signature on paper with its reason. Permission <c>handovers.manage</c>;
/// the counter steps (originals, signatures) also need <c>bookings.manage</c>: a preparer inspects only.
/// </summary>
/// <remarks>
/// A photo or a signature is read in memory and checked (real type, size) and stored before the database change that
/// references it; the stored file is deleted if the change fails, and the files a change releases (retaken photos,
/// voided signatures) once it is committed.
/// </remarks>
public sealed class HandoverInspectionService
{
    private readonly HandoverStore _store;

    internal HandoverInspectionService(HandoverStore store)
    {
        _store = store;
    }

    public Task<Result> RecordReadingsAsync(Guid inspectionId, ReadingsInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var accessories = AccessoryChecklist.Create(
            input.SpareWheel,
            input.Jack,
            input.WarningTriangle,
            input.SafetyVest,
            input.Keys,
            input.ChildSeats,
            input.Gps,
            input.VehiclePapers);
        if (accessories.IsFailure)
        {
            return Task.FromResult<Result>(accessories.Error);
        }

        return _store.ChangeAsync(inspectionId, (inspection, _) => inspection.RecordReadings(input.Mileage, input.FuelEighths, accessories.Value), cancellationToken);
    }

    /// <summary>The originals seen at the counter (departure): one entry per driver of the file.</summary>
    public Task<Result> RecordOriginalsAsync(Guid inspectionId, IReadOnlyList<DriverOriginals> originals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originals);
        return AtCounter()
            ? _store.ChangeAsync(inspectionId, (inspection, booking) => inspection.RecordOriginals(originals, booking.Parties.Drivers), cancellationToken)
            : Forbidden();
    }

    public Task<Result> SetCleaningNeededAsync(Guid inspectionId, bool needsCleaning, CancellationToken cancellationToken = default) =>
        _store.ChangeAsync(inspectionId, (inspection, _) => inspection.SetCleaningNeeded(needsCleaning), cancellationToken);

    public Task<Result> SetNotesAsync(Guid inspectionId, string? notes, CancellationToken cancellationToken = default) =>
        _store.ChangeAsync(inspectionId, (inspection, _) => inspection.SetNotes(notes), cancellationToken);

    /// <summary>
    /// Adds a photo (JPEG, PNG or WebP, 10 MB at most) and its thumbnail (1 MB at most) to a view of the inspection; a
    /// guided view is retaken. A damage close-up refers to <paramref name="damageMarkId"/>. Returns the photo id.
    /// </summary>
    public async Task<Result<Guid>> UploadPhotoAsync(
        Guid inspectionId,
        PhotoSlot slot,
        Stream photo,
        Stream? thumbnail,
        Guid? damageMarkId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        if (!_store.User.HasPermission(Permissions.HandoversManage))
        {
            return BookingErrors.Forbidden;
        }

        var image = await HandoverImage.ReadAsync(photo, HandoverMedia.MaxPhotoLength, pngOnly: false, cancellationToken);
        if (image.IsFailure)
        {
            return image.Error;
        }

        HandoverImage? small = null;
        if (thumbnail is not null)
        {
            var read = await HandoverImage.ReadAsync(thumbnail, HandoverMedia.MaxSmallImageLength, pngOnly: false, cancellationToken);
            if (read.IsFailure)
            {
                return read.Error;
            }

            small = read.Value;
        }

        var located = await _store.FindAsync(inspectionId, tracked: false, cancellationToken);
        if (located is null)
        {
            return HandoverErrors.InspectionNotFound;
        }

        var photoId = Entity.NewId();
        var path = HandoverMedia.PhotoPath(located.BookingId, inspectionId, photoId, image.Value.ContentType, thumbnail: false);
        var smallPath = small is null ? null : HandoverMedia.PhotoPath(located.BookingId, inspectionId, photoId, small.ContentType, thumbnail: true);
        await _store.StoreAsync(FileContainers.Inspections, path, image.Value.Content, image.Value.ContentType, cancellationToken);
        if (small is not null)
        {
            await _store.StoreAsync(FileContainers.Inspections, smallPath!, small.Content, small.ContentType, cancellationToken);
        }

        var added = await _store.ChangeAsync(
            inspectionId,
            (inspection, _, _) => Task.FromResult(
                inspection.AddPhoto(photoId, slot, image.Value.ToFile(path), small?.ToFile(smallPath!), damageMarkId, _store.Actor(), _store.Now()).Map(p => p.Id)),
            cancellationToken);
        if (added.IsFailure)
        {
            await _store.DeleteAsync(FileContainers.Inspections, smallPath is null ? [path] : [path, smallPath]);
        }

        return added;
    }

    public Task<Result> RemovePhotoAsync(Guid inspectionId, Guid photoId, CancellationToken cancellationToken = default) =>
        _store.ChangeAsync(inspectionId, (inspection, _) => inspection.RemovePhoto(photoId), cancellationToken);

    /// <summary>Marks a damage on the schematic; returns its id.</summary>
    public Task<Result<Guid>> AddDamageAsync(Guid inspectionId, DamageInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return _store.ChangeAsync(
            inspectionId,
            (inspection, _, _) => Task.FromResult(inspection.AddDamage(input, _store.Actor(), _store.Now()).Map(d => d.Id)),
            cancellationToken);
    }

    public Task<Result> UpdateDamageAsync(Guid inspectionId, Guid damageId, DamageInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return _store.ChangeAsync(inspectionId, (inspection, _) => inspection.UpdateDamage(damageId, input), cancellationToken);
    }

    public Task<Result> RemoveDamageAsync(Guid inspectionId, Guid damageId, CancellationToken cancellationToken = default) =>
        _store.ChangeAsync(inspectionId, (inspection, _) => inspection.RemoveDamage(damageId), cancellationToken);

    /// <summary>Records the signature of <paramref name="role"/> drawn on the tablet (a PNG image of 1 MB at most).</summary>
    public async Task<Result> SignAsync(Guid inspectionId, SignatureRole role, string? signerName, Stream png, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (!_store.User.HasPermission(Permissions.HandoversManage) || !AtCounter())
        {
            return BookingErrors.Forbidden;
        }

        var image = await HandoverImage.ReadAsync(png, HandoverMedia.MaxSmallImageLength, pngOnly: true, cancellationToken);
        if (image.IsFailure)
        {
            return image.Error;
        }

        var located = await _store.FindAsync(inspectionId, tracked: false, cancellationToken);
        if (located is null)
        {
            return HandoverErrors.InspectionNotFound;
        }

        var signatureId = Entity.NewId();
        var path = HandoverMedia.SignaturePath(located.BookingId, inspectionId, signatureId);
        await _store.StoreAsync(FileContainers.Inspections, path, image.Value.Content, image.Value.ContentType, cancellationToken);
        var signed = await _store.ChangeAsync(
            inspectionId,
            (inspection, _) => inspection.Sign(signatureId, role, signerName, image.Value.ToFile(path), _store.Actor(), _store.Now()),
            cancellationToken);
        if (signed.IsFailure)
        {
            await _store.DeleteAsync(FileContainers.Inspections, [path]);
        }

        return signed;
    }

    /// <summary>The accessible fallback: the contract is signed on paper, with the reason.</summary>
    public Task<Result> SignOnPaperAsync(Guid inspectionId, string? reason, CancellationToken cancellationToken = default) =>
        AtCounter()
            ? _store.ChangeAsync(inspectionId, (inspection, _) => inspection.SignOnPaper(reason, _store.Actor(), _store.Now()), cancellationToken)
            : Forbidden();

    public Task<Result> CancelPaperSignatureAsync(Guid inspectionId, CancellationToken cancellationToken = default) =>
        AtCounter()
            ? _store.ChangeAsync(inspectionId, (inspection, _) => inspection.CancelPaperSignature(), cancellationToken)
            : Forbidden();

    /// <summary>The counter steps (originals, signatures) are the agent's: <c>bookings.manage</c>.</summary>
    private bool AtCounter() => _store.User.HasPermission(Permissions.BookingsManage);

    private static Task<Result> Forbidden() => Task.FromResult<Result>(BookingErrors.Forbidden);
}
