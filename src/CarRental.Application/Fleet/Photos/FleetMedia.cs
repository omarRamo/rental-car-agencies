using System.Globalization;
using CarRental.Application.Common.Files;
using CarRental.Domain.Fleet;

namespace CarRental.Application.Fleet.Photos;

/// <summary>
/// Where fleet photos live: the private blob container <see cref="Container"/> (created on the first upload) and the
/// application URLs that stream them (ADR-0022). Category photos are public (<c>PhotoUrl</c> of the Fleet contract),
/// vehicle photos are reserved to back office users holding <c>fleet.read</c>.
/// </summary>
public static class FleetMedia
{
    /// <summary>Private container of the fleet photos.</summary>
    public const string Container = FileContainers.Fleet;

    /// <summary>Route of the public photo of a category (anonymous, cached).</summary>
    public const string CategoryPhotoRoute = "/media/categories/{categoryId:guid}";

    /// <summary>Route of a vehicle photo (back office only).</summary>
    public const string VehiclePhotoRoute = "/media/vehicles/{vehicleId:guid}/photos/{photoId:guid}";

    /// <summary>App-relative URL of a category photo; the version (content hash prefix) changes with the photo, so the URL can be cached.</summary>
    public static string CategoryPhotoUrl(Guid categoryId, string version) =>
        string.Create(CultureInfo.InvariantCulture, $"/media/categories/{categoryId:D}?v={version}");

    public static string VehiclePhotoUrl(Guid vehicleId, Guid photoId, string contentHash) =>
        string.Create(CultureInfo.InvariantCulture, $"/media/vehicles/{vehicleId:D}/photos/{photoId:D}?v={contentHash[..16]}");

    internal static string CategoryPhotoPath(Guid categoryId, string contentHash, string contentType) =>
        string.Create(CultureInfo.InvariantCulture, $"categories/{categoryId:D}/{contentHash[..16]}.{StoredPhoto.ExtensionOf(contentType)}");

    internal static string VehiclePhotoPath(Guid vehicleId, Guid photoId, string contentType) =>
        string.Create(CultureInfo.InvariantCulture, $"vehicles/{vehicleId:D}/{photoId:D}.{StoredPhoto.ExtensionOf(contentType)}");
}
