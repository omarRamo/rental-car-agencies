using CarRental.Application.Customers.Contracts;
using CarRental.Application.Customers.Documents;
using CarRental.Application.Customers.Privacy;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace CarRental.Web.Endpoints;

/// <summary>
/// HTTP endpoints of the Customers module (back office). Identity document images are only ever read here (ADR-0026):
/// permission <c>customers.documents.read</c> with an MFA session, every access logged by <see cref="DocumentImageAccess"/>
/// before the response, never cached (<c>no-store</c>), never shown under a guessable public URL.
/// </summary>
public static class CustomerEndpoints
{
    public const string Prefix = "/admin/customers";

    /// <summary>The image of one side of a document (<c>front</c> or <c>back</c>), streamed inline.</summary>
    public static string ImageUrl(Guid documentId, DocumentImageSideInfo side, string purpose) =>
        $"{Prefix}/documents/{documentId}/images/{SideSegment(side)}?purpose={Uri.EscapeDataString(purpose)}";

    /// <summary>JSON <c>{ url, expiresAtUtc, contentType }</c>: a read link valid 5 minutes.</summary>
    public static string TemporaryLinkUrl(Guid documentId, DocumentImageSideInfo side, string purpose) =>
        $"{Prefix}/documents/{documentId}/images/{SideSegment(side)}/link?purpose={Uri.EscapeDataString(purpose)}";

    /// <summary>The personal data export of a person (JSON attachment), for the manager.</summary>
    public static string ExportUrl(Guid personId, bool includeInternalRecords = false) =>
        $"{Prefix}/persons/{personId}/export{(includeInternalRecords ? "?internal=true" : string.Empty)}";

    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(Prefix).ExcludeFromDescription();

        group.MapGet("/documents/{documentId:guid}/images/{side}", ViewImageAsync)
            .RequireAuthorization(PermissionPolicy.For(Permissions.IdentityDocumentsRead));
        group.MapGet("/documents/{documentId:guid}/images/{side}/link", TemporaryLinkAsync)
            .RequireAuthorization(PermissionPolicy.For(Permissions.IdentityDocumentsRead));
        group.MapGet("/persons/{personId:guid}/export", ExportAsync)
            .RequireAuthorization(PermissionPolicy.For(Permissions.CustomersPrivacy));
        return endpoints;
    }

    private static async Task<IResult> ViewImageAsync(
        Guid documentId,
        string side,
        [FromQuery] string? purpose,
        [FromServices] DocumentImageAccess access,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (ParseSide(side) is not { } imageSide)
        {
            return TypedResults.NotFound();
        }

        var opened = await access.OpenAsync(documentId, imageSide, purpose, cancellationToken);
        if (opened.IsFailure)
        {
            return Problem(opened.Error);
        }

        var image = opened.Value;
        context.Response.Headers.CacheControl = "no-store, private";
        context.Response.Headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("inline") { FileName = image.FileName }.ToString();

        // The stream is disposed by the result once written.
        return TypedResults.Stream(image.Content, image.ContentType);
    }

    private static async Task<IResult> TemporaryLinkAsync(
        Guid documentId,
        string side,
        [FromQuery] string? purpose,
        [FromServices] DocumentImageAccess access,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (ParseSide(side) is not { } imageSide)
        {
            return TypedResults.NotFound();
        }

        var link = await access.CreateTemporaryLinkAsync(documentId, imageSide, purpose, cancellationToken);
        if (link.IsFailure)
        {
            return Problem(link.Error);
        }

        context.Response.Headers.CacheControl = "no-store, private";
        return TypedResults.Ok(new TemporaryLinkResponse(link.Value.Uri, link.Value.ExpiresAtUtc, link.Value.ContentType));
    }

    private static async Task<IResult> ExportAsync(
        Guid personId,
        [FromQuery(Name = "internal")] bool? includeInternalRecords,
        [FromServices] PersonalDataService personalData,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var export = await personalData.ExportAsync(personId, includeInternalRecords ?? false, cancellationToken);
        if (export.IsFailure)
        {
            return Problem(export.Error);
        }

        context.Response.Headers.CacheControl = "no-store, private";
        return TypedResults.Bytes(export.Value.Content, export.Value.ContentType, export.Value.FileName);
    }

    private static DocumentImageSideInfo? ParseSide(string side) => side switch
    {
        "front" => DocumentImageSideInfo.Front,
        "back" => DocumentImageSideInfo.Back,
        _ => null,
    };

    private static string SideSegment(DocumentImageSideInfo side) => side == DocumentImageSideInfo.Back ? "back" : "front";

    /// <summary>A business error as a status code; the detail is the stable error code only (no personal data).</summary>
    private static ProblemHttpResult Problem(Error error) => TypedResults.Problem(
        detail: error.Code,
        statusCode: error.Kind switch
        {
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError,
        });

    /// <summary>Response of the temporary link endpoint.</summary>
    public sealed record TemporaryLinkResponse(Uri Url, DateTimeOffset ExpiresAtUtc, string ContentType);
}
