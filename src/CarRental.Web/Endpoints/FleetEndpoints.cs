using CarRental.Application.Fleet.Deadlines;
using CarRental.Application.Fleet.Photos;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Components.Admin.Fleet;
using CarRental.Web.Security;
using Microsoft.Net.Http.Headers;

namespace CarRental.Web.Endpoints;

/// <summary>
/// Web parts of the Fleet module (ADR-0022): the photo streams and the deadline e-mail factory. Category photos are
/// public and cacheable (their URL carries the content version), whatever the agency; vehicle photos need
/// <c>fleet.read</c>, are served to the staff of the agency of the vehicle only (another agency's: not found, ADR-0067)
/// and are never stored by shared caches. Both are streamed from the private container with an entity tag (content hash).
/// </summary>
internal static class FleetEndpoints
{
    /// <summary>Category photos change URL when they change: a day of cache is safe.</summary>
    public const string PublicCacheControl = "public, max-age=86400";

    public const string PrivateCacheControl = "private, no-cache";

    public static IServiceCollection AddFleetWeb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IDeadlineAlertEmailFactory, DeadlineAlertEmailFactory>();
        return services;
    }

    public static IEndpointRouteBuilder MapFleetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        // Anonymous (public site), so rate limited per client address: each miss of the caches is a query and a blob read.
        endpoints.MapGet(FleetMedia.CategoryPhotoRoute, GetCategoryPhotoAsync).AllowAnonymous().RequireRateLimiting(SecurityRateLimits.Media);
        endpoints.MapGet(FleetMedia.VehiclePhotoRoute, GetVehiclePhotoAsync).RequireAuthorization(PermissionPolicy.For(Permissions.FleetRead));
        return endpoints;
    }

    private static async Task<IResult> GetCategoryPhotoAsync(Guid categoryId, HttpContext context, FleetPhotoService photos, CancellationToken cancellationToken)
    {
        var photo = await photos.FindCategoryPhotoAsync(categoryId, cancellationToken);
        return photo is null ? Results.NotFound() : await StreamAsync(photo, PublicCacheControl, context, photos, cancellationToken);
    }

    private static async Task<IResult> GetVehiclePhotoAsync(
        Guid vehicleId,
        Guid photoId,
        HttpContext context,
        FleetPhotoService photos,
        CancellationToken cancellationToken)
    {
        var photo = await photos.FindVehiclePhotoAsync(vehicleId, photoId, cancellationToken);
        if (photo.IsFailure)
        {
            return photo.Error.Kind == ErrorKind.Forbidden ? Results.Forbid() : Results.NotFound();
        }

        return await StreamAsync(photo.Value, PrivateCacheControl, context, photos, cancellationToken);
    }

    private static async Task<IResult> StreamAsync(
        PhotoDescriptor photo,
        string cacheControl,
        HttpContext context,
        FleetPhotoService photos,
        CancellationToken cancellationToken)
    {
        var entityTag = new EntityTagHeaderValue($"\"{photo.ContentHash}\"");
        context.Response.Headers.CacheControl = cacheControl;
        context.Response.Headers.ETag = entityTag.ToString();

        var ifNoneMatch = context.Request.GetTypedHeaders().IfNoneMatch;
        if (ifNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(entityTag, useStrongComparison: false)))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var download = await photos.OpenAsync(photo, cancellationToken);
        if (download is null)
        {
            return Results.NotFound();
        }

        context.Response.RegisterForDisposeAsync(download);
        return Results.Stream(download.Content, photo.ContentType);
    }
}
