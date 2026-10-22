using CarRental.Application.Bookings.Handover;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using Microsoft.Net.Http.Headers;

namespace CarRental.Web.Components.Admin.Handovers;

/// <summary>
/// Web parts of the departure and return (ADR-0041): the contract PDF (QuestPDF, Community licence set once here at
/// startup), the contract e-mail, and the streams of the hand-over files — photos and contracts for the readers of the
/// files (<c>bookings.read</c>), signatures for the hand-over staff (<c>handovers.manage</c>) — from the private
/// containers, never cached by a shared cache. The photos of the vehicle are revalidated by the browser (many per
/// hand-over); the contracts and the signatures hold personal data and are never cached, the browser included.
/// </summary>
internal static class HandoversWeb
{
    public static IServiceCollection AddHandoversWeb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        QuestPdfLicense.Configure();
        services.AddScoped<IRentalContractRenderer, RentalContractPdf>();
        services.AddScoped<IRentalContractEmailFactory, RentalContractEmailFactory>();
        return services;
    }

    public static IEndpointRouteBuilder MapHandoverEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(HandoverMedia.PhotoRoute, GetPhotoAsync).RequireAuthorization(PermissionPolicy.For(Permissions.BookingsRead)).ExcludeFromDescription();
        endpoints.MapGet(HandoverMedia.SignatureRoute, GetSignatureAsync).RequireAuthorization(PermissionPolicy.For(Permissions.HandoversManage)).ExcludeFromDescription();
        endpoints.MapGet(HandoverMedia.ContractRoute, GetContractAsync).RequireAuthorization(PermissionPolicy.For(Permissions.BookingsRead)).ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> GetPhotoAsync(Guid photoId, string? size, HttpContext context, HandoverQueries queries, CancellationToken cancellationToken) =>
        await StreamAsync(await queries.FindPhotoAsync(photoId, string.Equals(size, "thumb", StringComparison.Ordinal), cancellationToken), context, queries, personalData: false, cancellationToken);

    private static async Task<IResult> GetSignatureAsync(Guid signatureId, HttpContext context, HandoverQueries queries, CancellationToken cancellationToken) =>
        await StreamAsync(await queries.FindSignatureAsync(signatureId, cancellationToken), context, queries, personalData: true, cancellationToken);

    private static async Task<IResult> GetContractAsync(Guid contractId, HttpContext context, HandoverQueries queries, CancellationToken cancellationToken) =>
        await StreamAsync(await queries.FindContractAsync(contractId, cancellationToken), context, queries, personalData: true, cancellationToken);

    /// <summary>
    /// A hand-over file: with personal data (contract, signature), never cached; otherwise (a photo of the vehicle) kept
    /// privately by the browser and revalidated with its entity tag.
    /// </summary>
    private static async Task<IResult> StreamAsync(
        Result<HandoverMediaFile> found,
        HttpContext context,
        HandoverQueries queries,
        bool personalData,
        CancellationToken cancellationToken)
    {
        if (found.IsFailure)
        {
            return found.Error.Kind == ErrorKind.Forbidden ? Results.Forbid() : Results.NotFound();
        }

        var file = found.Value;
        if (personalData)
        {
            context.Response.Headers.CacheControl = "no-store, private";
        }
        else
        {
            var entityTag = new EntityTagHeaderValue($"\"{file.ETag}\"");
            context.Response.Headers.CacheControl = "private, no-cache";
            context.Response.Headers.ETag = entityTag.ToString();
            if (context.Request.GetTypedHeaders().IfNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(entityTag, useStrongComparison: false)))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
        }

        var download = await queries.OpenAsync(file, cancellationToken);
        if (download is null)
        {
            return Results.NotFound();
        }

        context.Response.RegisterForDisposeAsync(download);
        context.Response.Headers[HeaderNames.ContentDisposition] =
            new ContentDispositionHeaderValue("inline") { FileName = file.FileName }.ToString();
        return Results.Stream(download.Content, file.ContentType);
    }
}
