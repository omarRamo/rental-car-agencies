using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Customers.Documents;

/// <summary>
/// The only way to read an identity document image (invariant 9): permission <c>customers.documents.read</c>, and every
/// access — streamed view or 5-minute read link (user delegation SAS in Azure) — is written to the document access log
/// with the user, the time, the document, the purpose and the agency before anything is returned (ADR-0026, ADR-0067).
/// </summary>
public sealed class DocumentImageAccess(
    IAppDbContext db,
    IFileStorage storage,
    IDocumentAccessLog accessLog,
    IUserContext userContext,
    TimeProvider timeProvider)
{
    /// <summary>Validity of a temporary read link.</summary>
    public static readonly TimeSpan TemporaryLinkValidity = TimeSpan.FromMinutes(5);

    public const string ViewAccess = "view";
    public const string TemporaryLinkAccess = "temporary_link";

    /// <summary>Opens the image for streaming; the access is logged first. Dispose the result.</summary>
    public async Task<Result<DocumentImageContent>> OpenAsync(
        Guid documentId,
        DocumentImageSideInfo side,
        string? purpose,
        CancellationToken cancellationToken = default)
    {
        var target = await FindAsync(documentId, side, purpose, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error;
        }

        var image = target.Value;
        var download = await storage.OpenReadAsync(FileContainers.Documents, image.Path, cancellationToken);
        if (download is null)
        {
            return CustomersErrors.ImageNotFound;
        }

        try
        {
            await accessLog.RecordAsync(documentId, image.Kind, ViewAccess, image.Purpose, cancellationToken);
        }
        catch
        {
            await download.DisposeAsync();
            throw;
        }

        return new DocumentImageContent(download.Content, image.ContentType, download.Length, image.FileName);
    }

    /// <summary>A read link valid for <see cref="TemporaryLinkValidity"/>; the access is logged before the link is returned.</summary>
    public async Task<Result<TemporaryDocumentLink>> CreateTemporaryLinkAsync(
        Guid documentId,
        DocumentImageSideInfo side,
        string? purpose,
        CancellationToken cancellationToken = default)
    {
        var target = await FindAsync(documentId, side, purpose, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error;
        }

        var image = target.Value;
        var expiresAt = timeProvider.GetUtcNow() + TemporaryLinkValidity;
        var uri = await storage.GetTemporaryReadUriAsync(FileContainers.Documents, image.Path, TemporaryLinkValidity, cancellationToken);
        await accessLog.RecordAsync(documentId, image.Kind, TemporaryLinkAccess, image.Purpose, cancellationToken);
        return new TemporaryDocumentLink(uri, expiresAt, image.ContentType);
    }

    private async Task<Result<ImageTarget>> FindAsync(Guid documentId, DocumentImageSideInfo side, string? purpose, CancellationToken cancellationToken)
    {
        // Viewed only within the agency of its customer, whose journal records the access (ADR-0067): an operation
        // across agencies (the support outside any agency) enters the agency first.
        if (!userContext.HasPermission(Permissions.IdentityDocumentsRead) || userContext.AgencyId is null)
        {
            return CustomersErrors.Forbidden;
        }

        if (!Enum.IsDefined(side))
        {
            return CustomersErrors.ImageSideInvalid;
        }

        if (DocumentAccessPurposes.Normalize(purpose) is not { } reason)
        {
            return CustomersErrors.AccessPurposeInvalid;
        }

        // A document of a person, or of a public submission waiting for an agent (ADR-0050): same viewer, same log. Of the
        // agency of the operation only, which the log records.
        var agencyId = userContext.AgencyId.Value;
        var document = await db.Set<Person>()
            .AsNoTracking()
            .Where(p => p.AgencyId == agencyId && p.AnonymizedAtUtc == null)
            .SelectMany(p => p.Documents)
            .Where(d => d.Id == documentId)
            .Select(d => new { d.Type, d.FrontImage, d.BackImage })
            .FirstOrDefaultAsync(cancellationToken)
            ?? await db.Set<DriverSubmission>()
                .AsNoTracking()
                .Where(s => s.AgencyId == agencyId && s.State == DriverSubmissionState.Pending)
                .SelectMany(s => s.Documents)
                .Where(d => d.Id == documentId)
                .Select(d => new { d.Type, d.FrontImage, d.BackImage })
                .FirstOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return CustomersErrors.DocumentNotFound;
        }

        var domainSide = side.ToDomain();
        var image = domainSide == DocumentImageSide.Front ? document.FrontImage : document.BackImage;
        if (image is null)
        {
            return CustomersErrors.ImageNotFound;
        }

        var extension = Path.GetExtension(image.Path);
        var fileName = $"{CustomerMapping.AccessLogKind(document.Type, domainSide).Replace('.', '-')}{extension}";
        return new ImageTarget(image.Path, image.ContentType, CustomerMapping.AccessLogKind(document.Type, domainSide), reason, fileName);
    }

    private sealed record ImageTarget(string Path, string ContentType, string Kind, string Purpose, string FileName);
}

/// <summary>An identity document image opened for streaming. The caller disposes it (or hands it to the response).</summary>
public sealed class DocumentImageContent(Stream content, string contentType, long length, string fileName) : IAsyncDisposable, IDisposable
{
    public Stream Content { get; } = content;

    /// <summary>The detected content type: <c>image/jpeg</c>, <c>image/png</c> or <c>application/pdf</c>.</summary>
    public string ContentType { get; } = contentType;

    public long Length { get; } = length;

    /// <summary>Neutral file name (no personal data), e.g. <c>passport-front.jpg</c>.</summary>
    public string FileName { get; } = fileName;

    public ValueTask DisposeAsync() => Content.DisposeAsync();

    public void Dispose() => Content.Dispose();
}

/// <summary>A short-lived read link to an identity document image (5 minutes).</summary>
public sealed record TemporaryDocumentLink(Uri Uri, DateTimeOffset ExpiresAtUtc, string ContentType);
