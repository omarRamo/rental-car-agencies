using CarRental.Application.Billing.Administration;
using CarRental.Application.Billing.Claims;
using CarRental.Application.Billing.Documents;
using CarRental.Application.Billing.Fines;
using CarRental.Application.Common.Files;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using CarRental.Web.Components.Admin.Handovers;
using Microsoft.Net.Http.Headers;

namespace CarRental.Web.Components.Admin.Billing;

/// <summary>
/// Web parts of the Billing module (ADR-0058, ADR-0059): the PDF renderers (QuestPDF), the e-mail factory and the texts of
/// the documents, and the streams of the back office — the stored PDF of an invoice or a credit note (and the courtesy
/// translation of an invoice, rendered on demand) for the readers of invoices, the designation form of a fine (personal
/// data, produced and logged on each request) and the files of the claims — always with an MFA session, never cached by
/// a shared cache.
/// </summary>
internal static class BillingWeb
{
    public static IServiceCollection AddBillingWeb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        QuestPdfLicense.Configure();
        services.AddScoped<IBillingTexts, BillingTexts>();
        services.AddScoped<IBillingDocumentRenderer, BillingDocumentPdf>();
        services.AddScoped<IFineDesignationRenderer, FineDesignationPdf>();
        services.AddScoped<IBillingEmailFactory, BillingEmailFactory>();
        return services;
    }

    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/admin/invoices/{invoiceId:guid}/pdf", GetInvoicePdfAsync).RequireAuthorization(PermissionPolicy.For(Permissions.InvoicesRead)).ExcludeFromDescription();
        endpoints.MapGet("/admin/credit-notes/{creditNoteId:guid}/pdf", GetCreditNotePdfAsync).RequireAuthorization(PermissionPolicy.For(Permissions.InvoicesRead)).ExcludeFromDescription();
        endpoints.MapGet("/admin/fines/{fineId:guid}/designation", GetDesignationAsync).RequireAuthorization(PermissionPolicy.For(Permissions.FinesManage)).ExcludeFromDescription();
        endpoints.MapGet("/admin/claims/files/{documentId:guid}", GetClaimFileAsync).RequireAuthorization(PermissionPolicy.For(Permissions.ClaimsManage)).ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> GetInvoicePdfAsync(
        Guid invoiceId,
        string? lang,
        HttpContext context,
        BillingAdministrationQueries queries,
        BillingDocumentPipeline pipeline,
        CancellationToken cancellationToken)
    {
        if (string.Equals(lang, "en", StringComparison.Ordinal))
        {
            // A courtesy translation, rendered now and never stored: only for those who may read the original.
            var original = await queries.FindPdfAsync(BillingDocumentType.Invoice, invoiceId, cancellationToken);
            if (original.IsFailure && original.Error != BillingErrors.PdfNotReady)
            {
                return original.Error.Kind == ErrorKind.Forbidden ? Results.Forbid() : Results.NotFound();
            }

            var pdf = await pipeline.RenderAsync(BillingDocumentType.Invoice, invoiceId, "en", cancellationToken);
            if (pdf is null)
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("inline") { FileName = $"invoice-{invoiceId:N}-en.pdf" }.ToString();
            return Results.File(pdf, BillingFormat.PdfContentType);
        }

        return await StreamAsync(await queries.FindPdfAsync(BillingDocumentType.Invoice, invoiceId, cancellationToken), context, queries, cancellationToken);
    }

    private static async Task<IResult> GetCreditNotePdfAsync(Guid creditNoteId, HttpContext context, BillingAdministrationQueries queries, CancellationToken cancellationToken) =>
        await StreamAsync(await queries.FindPdfAsync(BillingDocumentType.CreditNote, creditNoteId, cancellationToken), context, queries, cancellationToken);

    private static async Task<IResult> GetDesignationAsync(Guid fineId, HttpContext context, FineService fines, CancellationToken cancellationToken)
    {
        var form = await fines.ProduceDesignationFormAsync(fineId, cancellationToken);
        if (form.IsFailure)
        {
            return form.Error.Kind switch
            {
                ErrorKind.Forbidden => Results.Forbid(),
                ErrorKind.NotFound => Results.NotFound(),
                _ => Results.Conflict(),
            };
        }

        // Personal data: never kept by a cache, the browser included.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("inline") { FileName = form.Value.FileName }.ToString();
        return Results.File(form.Value.Content, BillingFormat.PdfContentType);
    }

    private static async Task<IResult> GetClaimFileAsync(Guid documentId, HttpContext context, ClaimService claims, CancellationToken cancellationToken)
    {
        var found = await claims.FindFileAsync(documentId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error.Kind == ErrorKind.Forbidden ? Results.Forbid() : Results.NotFound();
        }

        var file = found.Value;
        if (NotModified(context, file.ETag) is { } notModified)
        {
            return notModified;
        }

        var download = await claims.OpenAsync(file, cancellationToken);
        return download is null ? Results.NotFound() : Stream(context, download, file.ContentType, file.FileName);
    }

    private static async Task<IResult> StreamAsync(Result<BillingPdfFile> found, HttpContext context, BillingAdministrationQueries queries, CancellationToken cancellationToken)
    {
        if (found.IsFailure)
        {
            return found.Error.Kind == ErrorKind.Forbidden ? Results.Forbid() : Results.NotFound();
        }

        var file = found.Value;
        if (NotModified(context, file.ETag) is { } notModified)
        {
            return notModified;
        }

        var download = await queries.OpenAsync(file, cancellationToken);
        return download is null ? Results.NotFound() : Stream(context, download, file.ContentType, file.FileName);
    }

    /// <summary>Sets the private cache headers and answers 304 when the browser already has this version.</summary>
    private static IResult? NotModified(HttpContext context, string etag)
    {
        var entityTag = new EntityTagHeaderValue($"\"{etag}\"");
        context.Response.Headers.CacheControl = "private, no-cache";
        context.Response.Headers.ETag = entityTag.ToString();
        return context.Request.GetTypedHeaders().IfNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(entityTag, useStrongComparison: false))
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : null;
    }

    private static IResult Stream(HttpContext context, FileDownload download, string contentType, string fileName)
    {
        context.Response.RegisterForDisposeAsync(download);
        context.Response.Headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("inline") { FileName = fileName }.ToString();
        return Results.Stream(download.Content, contentType);
    }
}
