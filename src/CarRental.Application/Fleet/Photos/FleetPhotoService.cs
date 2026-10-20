using CarRental.Application.Common.Files;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Fleet.Photos;

/// <summary>A stored photo to stream: path in <see cref="FleetMedia.Container"/>, content type and entity tag (content hash).</summary>
public sealed record PhotoDescriptor(string Path, string ContentType, string ContentHash);

/// <summary>
/// Photos of the categories (public) and of the vehicles (back office only), in the private fleet container (ADR-0022).
/// An upload is read in memory (10 MB at most), its real type is checked from its signature, then it is stored before
/// the database change; a replaced or removed file is deleted after the commit.
/// </summary>
public sealed partial class FleetPhotoService(
    IAppDbContext db,
    FleetWriter writer,
    IFileStorage storage,
    IUserContext userContext,
    ILogger<FleetPhotoService> logger)
{
    public async Task<Result<Guid>> UploadVehiclePhotoAsync(Guid vehicleId, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!userContext.HasPermission(Permissions.FleetManage))
        {
            return FleetErrors.Forbidden;
        }

        var file = await PhotoFile.ReadAsync(content, cancellationToken);
        if (file.IsFailure)
        {
            return file.Error;
        }

        var vehicle = await db.Set<Vehicle>().AsNoTracking().Where(v => v.Id == vehicleId).Select(v => new { v.Status, Photos = v.Photos.Count }).FirstOrDefaultAsync(cancellationToken);
        if (vehicle is null)
        {
            return FleetErrors.VehicleNotFound;
        }

        if (vehicle.Status == VehicleStatus.Sold)
        {
            return FleetErrors.VehicleSold;
        }

        if (vehicle.Photos >= Vehicle.MaxPhotos)
        {
            return FleetErrors.TooManyPhotos;
        }

        var photoId = Entity.NewId();
        var path = FleetMedia.VehiclePhotoPath(vehicleId, photoId, file.Value.ContentType);
        await StoreAsync(path, file.Value, cancellationToken);

        var added = await writer.ChangeVehicleAsync(
            vehicleId,
            (aggregate, stamp, _) => Task.FromResult(aggregate.AddPhoto(photoId, file.Value.ToStoredPhoto(path), stamp).Map(p => p.Id)),
            cancellationToken);
        if (added.IsFailure)
        {
            await TryDeleteAsync(path);
        }

        return added;
    }

    public async Task<Result> RemoveVehiclePhotoAsync(Guid vehicleId, Guid photoId, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.FleetManage))
        {
            return FleetErrors.Forbidden;
        }

        var removed = await writer.ChangeVehicleAsync(
            vehicleId,
            (vehicle, stamp, _) => Task.FromResult(vehicle.RemovePhoto(photoId, stamp).Map(p => p.StoragePath)),
            cancellationToken);
        if (removed.IsFailure)
        {
            return removed.Error;
        }

        await TryDeleteAsync(removed.Value);
        return Result.Success();
    }

    public async Task<Result> UploadCategoryPhotoAsync(Guid categoryId, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!userContext.HasPermission(Permissions.FleetManage))
        {
            return FleetErrors.Forbidden;
        }

        var file = await PhotoFile.ReadAsync(content, cancellationToken);
        if (file.IsFailure)
        {
            return file.Error;
        }

        if (!await db.Set<VehicleCategory>().AnyAsync(c => c.Id == categoryId, cancellationToken))
        {
            return FleetErrors.CategoryNotFound;
        }

        var path = FleetMedia.CategoryPhotoPath(categoryId, file.Value.ContentHash, file.Value.ContentType);
        await StoreAsync(path, file.Value, cancellationToken);

        var replaced = await writer.ChangeCategoryAsync(
            categoryId,
            (category, _) =>
            {
                var previous = category.PhotoPath;
                category.SetPhoto(file.Value.ToStoredPhoto(path));
                return Task.FromResult(Result.Success(previous));
            },
            signalAvailability: false,
            cancellationToken);
        if (replaced.IsFailure)
        {
            await TryDeleteAsync(path);
            return replaced.Error;
        }

        if (replaced.Value is { } previousPath && !string.Equals(previousPath, path, StringComparison.Ordinal))
        {
            await TryDeleteAsync(previousPath);
        }

        return Result.Success();
    }

    public async Task<Result> RemoveCategoryPhotoAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.FleetManage))
        {
            return FleetErrors.Forbidden;
        }

        var removed = await writer.ChangeCategoryAsync(
            categoryId,
            (category, _) =>
            {
                var previous = category.PhotoPath;
                category.RemovePhoto();
                return Task.FromResult(Result.Success(previous));
            },
            signalAvailability: false,
            cancellationToken);
        if (removed.IsFailure)
        {
            return removed.Error;
        }

        if (removed.Value is { } previousPath)
        {
            await TryDeleteAsync(previousPath);
        }

        return Result.Success();
    }

    /// <summary>The public photo of a category (anonymous access), or <see langword="null"/>.</summary>
    public async Task<PhotoDescriptor?> FindCategoryPhotoAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        await db.Set<VehicleCategory>()
            .AsNoTracking()
            .Where(c => c.Id == categoryId && c.PhotoPath != null)
            .Select(c => new PhotoDescriptor(c.PhotoPath!, c.PhotoContentType!, c.PhotoHash!))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>A photo of a vehicle, for back office users holding <c>fleet.read</c>.</summary>
    public async Task<Result<PhotoDescriptor>> FindVehiclePhotoAsync(Guid vehicleId, Guid photoId, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.FleetRead))
        {
            return FleetErrors.Forbidden;
        }

        var photo = await db.Set<Vehicle>()
            .AsNoTracking()
            .Where(v => v.Id == vehicleId)
            .SelectMany(v => v.Photos)
            .Where(p => p.Id == photoId)
            .Select(p => new PhotoDescriptor(p.StoragePath, p.ContentType, p.ContentHash))
            .FirstOrDefaultAsync(cancellationToken);
        return photo is null ? FleetErrors.PhotoNotFound : photo;
    }

    public Task<FileDownload?> OpenAsync(PhotoDescriptor photo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        return storage.OpenReadAsync(FleetMedia.Container, photo.Path, cancellationToken);
    }

    private async Task StoreAsync(string path, PhotoFile file, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(file.Content, writable: false);
        await storage.UploadAsync(FleetMedia.Container, path, stream, file.ContentType, cancellationToken);
    }

    /// <summary>Best effort: an orphan file costs a few kilobytes, a failed request would be worse.</summary>
    private async Task TryDeleteAsync(string path)
    {
        try
        {
            await storage.DeleteAsync(FleetMedia.Container, path, CancellationToken.None);
        }
#pragma warning disable CA1031 // Cleanup of a stored file must never fail the request.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogDeleteFailed(exception, path);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet photo {Path} could not be deleted from storage.")]
    private partial void LogDeleteFailed(Exception exception, string path);
}
